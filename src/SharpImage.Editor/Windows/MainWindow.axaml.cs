using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SharpImage.Editor.Assets;
using SharpImage.Editor.Controls;
using SharpImage.Editor.Models;
using SharpImage.Editor.Services;
using SharpImage.Editor.Tools;
using SharpImage.Formats;

namespace SharpImage.Editor.Windows;

public partial class MainWindow : Window
{
    private readonly ToolService toolService = new();
    private readonly UndoService undoService = new();
    private readonly WorkspaceService workspaceService = new();
    private readonly SettingsService settingsService = new();

    private EditorDocument? document;
    private ColorSwatchControl? colorSwatch;
    private ImageCanvas? imageCanvas;

    /// <summary>When non-null, GetFilterSource() returns this instead of the active layer.
    /// Used by filter preview to apply filters to scaled-down preview frames.</summary>
    private SharpImage.Image.ImageFrame? filterPreviewSourceOverride;

    // Tool instances (created once, reused)
    private readonly Dictionary<EditorToolType, ITool> toolInstances = [];
    private Color foregroundColor = Colors.Black;
    private Color backgroundColor = Colors.White;

    // Panel references for visibility toggling
    private CollapsiblePanel? navigatorPanel;
    private CollapsiblePanel? colorPanel;
    private CollapsiblePanel? layersPanel;
    private CollapsiblePanel? historyPanel;
    private CollapsiblePanel? propertiesPanel;
    private CollapsiblePanel? channelsPanel;
    private CollapsiblePanel? brushesPanel;
    private CollapsiblePanel? adjustmentsPanel;

    // Space key temporary hand tool tracking
    private EditorToolType? toolBeforeSpaceHand;
    private bool isSpaceHeld;

    // History panel list for dynamic updates
    private StackPanel? historyList;

    // Layers panel dynamic content
    private StackPanel? layerListPanel;
    private ComboBox? layerBlendCombo;
    private TextBox? layerOpacityField;

    // Color panel fields
    private TextBox? colorRedField;
    private TextBox? colorGreenField;
    private TextBox? colorBlueField;
    private TextBox? colorHexField;

    // Navigator zoom slider (wired to canvas after canvas is built)
    private Slider? navigatorZoomSlider;
    private Avalonia.Controls.Image? navigatorPreviewImage;
    private Avalonia.Media.Imaging.WriteableBitmap? navigatorBitmap;

    // Properties panel content for dynamic updates
    private StackPanel? propertiesContent;

    // Pixel operation undo snapshot
    private SharpImage.Image.ImageFrame? pixelBeforeSnapshot;

    public MainWindow()
    {
        // Load persisted settings before building UI
        settingsService.Load();
        settingsService.ApplyTo(workspaceService);
        undoService.MaxUndoLevels = settingsService.Settings.UndoLevels;

        InitializeComponent();
        BuildToolInstances();
        BuildToolPalette();
        BuildPanelDock();
        BuildCanvas();
        WireMenuEvents();
        WireToolService();
        WireWorkspaceService();
        WireUndoService();
        UpdateStatusBar();
        UpdateUndoMenuState();
        UpdateTitle();
        SyncActiveTool();
        PopulateOptionsBar();

        // Restore window state
        var s = settingsService.Settings;
        if (!double.IsNaN(s.WindowWidth) && s.WindowWidth > 200)
            Width = s.WindowWidth;
        if (!double.IsNaN(s.WindowHeight) && s.WindowHeight > 200)
            Height = s.WindowHeight;
        if (s.IsMaximized)
            WindowState = WindowState.Maximized;

        // Open startup file if provided via command line
        if (App.StartupPath is not null)
            OpenFile(App.StartupPath);

        // Enable file drag-and-drop
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnFileDrop);

        Closing += OnWindowClosing;
    }

    // ═══════════════════════════════════════════════════
    //  Tool Palette
    // ═══════════════════════════════════════════════════

    private void BuildToolPalette()
    {
        // Show one button per primary tool (first in each group, or ungrouped)
        var shownGroups = new HashSet<string>();

        foreach (var def in ToolDefinitions.All)
        {
            if (def.GroupName is not null)
            {
                if (!shownGroups.Add(def.GroupName))
                    continue; // skip secondary tools in same group
            }

            var button = new Button
            {
                Classes = { "toolbar" },
                Width = 36,
                Height = 36,
                Tag = def.Type,
                Content = LucideIcon.CreateFromResource(this, def.IconResourceKey, 18),
            };
            Avalonia.Controls.ToolTip.SetTip(button, $"{def.DisplayName} ({def.Shortcut})");

            button.Click += OnToolButtonClick;
            ToolPalettePanel.Children.Add(button);
        }

        HighlightActiveTool();

        // Separator before color swatches
        ToolPalettePanel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(4, 6),
            [!Border.BackgroundProperty] = this.GetResourceObservable("SeparatorOpaqueBrush").ToBinding(),
        });

        // Foreground/Background color swatches
        colorSwatch = new ColorSwatchControl(toolService.Brush)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        colorSwatch.SwapRequested += () =>
        {
            toolService.Brush.SwapColors();
            (foregroundColor, backgroundColor) = (backgroundColor, foregroundColor);
            colorSwatch.SetColors(foregroundColor, backgroundColor);
            PropagateColorToTools();
            UpdateColorFields();
        };
        colorSwatch.ResetRequested += () =>
        {
            foregroundColor = Colors.Black;
            backgroundColor = Colors.White;
            toolService.Brush.ForegroundColor = 0xFF000000;
            toolService.Brush.BackgroundColor = 0xFFFFFFFF;
            colorSwatch.SetColors(foregroundColor, backgroundColor);
            PropagateColorToTools();
            UpdateColorFields();
        };
        ToolPalettePanel.Children.Add(colorSwatch);
    }

    private void OnToolButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EditorToolType type })
            toolService.ActiveToolType = type;
    }

    private void HighlightActiveTool()
    {
        foreach (var child in ToolPalettePanel.Children)
        {
            if (child is Button btn)
            {
                bool isActive = btn.Tag is EditorToolType type && type == toolService.ActiveToolType;
                if (isActive)
                {
                    btn.Background = this.FindResource("SelectedBgBrush") as IBrush;
                    // Re-create icon with white stroke for selected state
                    if (btn.Tag is EditorToolType selectedType)
                    {
                        var def = Array.Find(ToolDefinitions.All, d => d.Type == selectedType);
                        if (def.IconResourceKey is not null)
                        {
                            var pathData = this.FindResource(def.IconResourceKey) as string ?? "";
                            btn.Content = LucideIcon.Create(pathData, 18, Brushes.White);
                        }
                    }
                }
                else
                {
                    btn.Background = Brushes.Transparent;
                    if (btn.Tag is EditorToolType otherType)
                    {
                        var def = Array.Find(ToolDefinitions.All, d => d.Type == otherType);
                        if (def.IconResourceKey is not null)
                            btn.Content = LucideIcon.CreateFromResource(this, def.IconResourceKey, 18);
                    }
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════
    //  Tool Instances
    // ═══════════════════════════════════════════════════

    private void BuildToolInstances()
    {
        // Move
        var moveTool = new MoveTool();
        moveTool.LayerMoved += () => imageCanvas?.MarkDirty();
        moveTool.MoveCompleted += (oldX, oldY, newX, newY) =>
        {
            if (document is null) return;
            int layerIdx = document.ActiveLayerIndex;
            undoService.Push(new LayerPropertyCommand(
                "Move Layer", layerIdx,
                l => { l.OffsetX = oldX; l.OffsetY = oldY; },
                l => { l.OffsetX = newX; l.OffsetY = newY; }));
            document.IsDirty = true;
            UpdateTitle();
        };
        toolInstances[EditorToolType.Move] = moveTool;

        // Rectangular Marquee
        var rectMarquee = new RectangularMarqueeTool();
        rectMarquee.SelectionCompleted += rect =>
        {
            if (document is null) return;
            document.SelectRectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.RectangularMarquee] = rectMarquee;

        // Elliptical Marquee
        var ellipseMarquee = new EllipticalMarqueeTool();
        ellipseMarquee.SelectionCompleted += rect =>
        {
            if (document is null) return;
            int cx = (int)(rect.X + rect.Width / 2);
            int cy = (int)(rect.Y + rect.Height / 2);
            document.SelectEllipse(cx, cy, (int)(rect.Width / 2), (int)(rect.Height / 2));
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.EllipticalMarquee] = ellipseMarquee;

        // Lasso
        var lasso = new LassoTool();
        lasso.SelectionCompleted += points =>
        {
            if (document is null || points.Count < 3) return;
            var intPoints = new (int X, int Y)[points.Count];
            for (int i = 0; i < points.Count; i++)
                intPoints[i] = ((int)points[i].X, (int)points[i].Y);
            document.SelectPolygon(intPoints);
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.Lasso] = lasso;

        // Crop
        var cropTool = new CropTool();
        cropTool.CropChanged += () => imageCanvas?.InvalidateVisual();
        cropTool.CropApplied += rect =>
        {
            if (document is null) return;
            int cx = Math.Max(0, (int)Math.Round(rect.X));
            int cy = Math.Max(0, (int)Math.Round(rect.Y));
            int cw = Math.Min(document.Width - cx, (int)Math.Round(rect.Width));
            int ch = Math.Min(document.Height - cy, (int)Math.Round(rect.Height));
            if (cw <= 0 || ch <= 0) return;

            BeginPixelOperation();

            for (int i = 0; i < document.Layers.Count; i++)
            {
                var layer = document.Layers[i];
                if (layer.Content is null) continue;
                layer.Content = SharpImage.Transform.Geometry.Crop(layer.Content, cx, cy, cw, ch);
                layer.OffsetX = 0;
                layer.OffsetY = 0;
            }
            document.Resize(cw, ch);
            document.SelectionMask = null;
            document.IsDirty = true;

            CommitPixelChange($"Crop {cw}×{ch}");
            imageCanvas?.SetDocument(document);
            RefreshLayersPanel();
            UpdateStatusBar();
            UpdateTitle();
        };
        toolInstances[EditorToolType.Crop] = cropTool;

        // Eyedropper
        var eyedropper = new EyedropperTool();
        eyedropper.ColorSampled += (color, isForeground) =>
        {
            if (isForeground)
                foregroundColor = color;
            else
                backgroundColor = color;
            colorSwatch?.SetColors(foregroundColor, backgroundColor);
            UpdateColorFields();
            imageCanvas?.InvalidateVisual();
        };
        toolInstances[EditorToolType.Eyedropper] = eyedropper;

        // Hand & Zoom (existing)
        var handTool = new HandTool();
        handTool.PanRequested += delta =>
        {
            // Pan is handled by ImageCanvas directly via middle-click,
            // but HandTool also needs to pan when it's the active tool
            if (imageCanvas is not null)
            {
                // Access the pan offset field via a dedicated method
                imageCanvas.Pan(delta);
            }
        };
        toolInstances[EditorToolType.Hand] = handTool;

        var zoomTool = new ZoomTool();
        zoomTool.ZoomRequested += (point, factor) =>
        {
            imageCanvas?.SetZoom(imageCanvas.Zoom * factor, imageCanvas.ImageToScreen(point));
        };
        toolInstances[EditorToolType.Zoom] = zoomTool;

        // Brush
        var brushTool = new BrushTool();
        brushTool.SetBrushSettings(toolService.Brush);
        brushTool.StrokeStarted += () => BeginPixelOperation();
        brushTool.StrokeCompleted += () =>
        {
            if (document is null) return;
            CommitPixelChange("Brush Stroke");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.Brush] = brushTool;

        // Eraser
        var eraserTool = new EraserTool();
        eraserTool.SetBrushSettings(toolService.Brush);
        eraserTool.StrokeStarted += () => BeginPixelOperation();
        eraserTool.StrokeCompleted += () =>
        {
            if (document is null) return;
            CommitPixelChange("Eraser Stroke");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.Eraser] = eraserTool;

        // Paint Bucket — snapshot taken in FillStarted, committed in FillCompleted
        var bucketTool = new PaintBucketTool();
        bucketTool.FillStarted += () => BeginPixelOperation();
        bucketTool.FillCompleted += () =>
        {
            if (document is null) return;
            CommitPixelChange("Paint Bucket Fill");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.PaintBucket] = bucketTool;

        // Gradient — snapshot taken in GradientStarted, committed in GradientApplied
        var gradientTool = new GradientTool();
        gradientTool.SetBrushSettings(toolService.Brush);
        gradientTool.GradientStarted += () => BeginPixelOperation();
        gradientTool.GradientApplied += () =>
        {
            if (document is null) return;
            CommitPixelChange("Gradient");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.Gradient] = gradientTool;

        // Clone Stamp
        var cloneStamp = new CloneStampTool();
        cloneStamp.SetBrushSettings(toolService.Brush);
        cloneStamp.StrokeStarted += () => BeginPixelOperation();
        cloneStamp.StrokeCompleted += () =>
        {
            if (document is null) return;
            CommitPixelChange("Clone Stamp");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.CloneStamp] = cloneStamp;

        // Dodge/Burn/Sponge
        var dodgeBurn = new DodgeBurnTool();
        dodgeBurn.SetBrushSettings(toolService.Brush);
        dodgeBurn.StrokeStarted += () => BeginPixelOperation();
        dodgeBurn.StrokeCompleted += () =>
        {
            if (document is null) return;
            string desc = dodgeBurn.Mode switch
            {
                DodgeBurnTool.ToolMode.Dodge => "Dodge",
                DodgeBurnTool.ToolMode.Burn => "Burn",
                _ => "Sponge"
            };
            CommitPixelChange(desc);
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.Dodge] = dodgeBurn;
        toolInstances[EditorToolType.Burn] = dodgeBurn;
        toolInstances[EditorToolType.Sponge] = dodgeBurn;

        // Magic Wand
        var magicWand = new MagicWandTool();
        magicWand.SelectionCompleted += () => imageCanvas?.MarkDirty();
        toolInstances[EditorToolType.MagicWand] = magicWand;

        // Polygonal Lasso
        var polyLasso = new PolygonalLassoTool();
        polyLasso.SelectionCompleted += () => imageCanvas?.MarkDirty();
        toolInstances[EditorToolType.PolygonalLasso] = polyLasso;

        // Text
        var textTool = new TextTool();
        textTool.TextCommitted += () =>
        {
            if (document is null) return;
            CommitPixelChange("Text");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.Text] = textTool;

        // Shape
        var shapeTool = new ShapeTool();
        shapeTool.ShapeStarted += () => BeginPixelOperation();
        shapeTool.ShapeCompleted += () =>
        {
            if (document is null) return;
            CommitPixelChange("Shape");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.Shape] = shapeTool;

        // Blur Brush
        var blurBrush = new BlurBrushTool();
        blurBrush.SetBrushSettings(toolService.Brush);
        blurBrush.StrokeStarted += () => BeginPixelOperation();
        blurBrush.StrokeCompleted += () =>
        {
            if (document is null) return;
            CommitPixelChange("Blur Brush");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.BlurBrush] = blurBrush;

        // Sharpen Brush
        var sharpenBrush = new SharpenBrushTool();
        sharpenBrush.SetBrushSettings(toolService.Brush);
        sharpenBrush.StrokeStarted += () => BeginPixelOperation();
        sharpenBrush.StrokeCompleted += () =>
        {
            if (document is null) return;
            CommitPixelChange("Sharpen Brush");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.SharpenBrush] = sharpenBrush;

        // Smudge
        var smudgeTool = new SmudgeTool();
        smudgeTool.SetBrushSettings(toolService.Brush);
        smudgeTool.StrokeStarted += () => BeginPixelOperation();
        smudgeTool.StrokeCompleted += () =>
        {
            if (document is null) return;
            CommitPixelChange("Smudge");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.Smudge] = smudgeTool;

        // Healing Brush
        var healingBrush = new HealingBrushTool();
        healingBrush.SetBrushSettings(toolService.Brush);
        healingBrush.StrokeStarted += () => BeginPixelOperation();
        healingBrush.StrokeCompleted += () =>
        {
            if (document is null) return;
            CommitPixelChange("Healing Brush");
            imageCanvas?.MarkDirty();
        };
        toolInstances[EditorToolType.HealingBrush] = healingBrush;

        // Measure
        var measureTool = new MeasureTool();
        measureTool.MeasurementChanged += (dist, angle) =>
        {
            StatusCursorPos.Text = $"D: {dist:F1}px  A: {angle:F1}°";
        };
        toolInstances[EditorToolType.Measure] = measureTool;

        // Pen
        var penTool = new PenTool();
        penTool.PathCompleted += () => imageCanvas?.MarkDirty();
        toolInstances[EditorToolType.PenTool] = penTool;
    }

    private void SyncActiveTool()
    {
        var toolType = toolService.ActiveToolType;
        if (toolInstances.TryGetValue(toolType, out var tool))
        {
            // Give document-aware tools the current document
            if (tool is MoveTool mt) mt.SetDocument(document);
            if (tool is CropTool ct) ct.SetDocument(document);
            if (tool is EyedropperTool et) et.SetDocument(document);
            if (tool is BrushTool bt) { bt.SetDocument(document); bt.SetColor(foregroundColor); }
            if (tool is EraserTool ert) ert.SetDocument(document);
            if (tool is PaintBucketTool pbt) { pbt.SetDocument(document); pbt.SetColor(foregroundColor); }
            if (tool is GradientTool gt) { gt.SetDocument(document); gt.SetColors(foregroundColor, backgroundColor); }
            if (tool is CloneStampTool cst) cst.SetDocument(document);
            if (tool is DodgeBurnTool dbt) dbt.SetDocument(document);
            if (tool is MagicWandTool mwt) mwt.SetDocument(document);
            if (tool is PolygonalLassoTool plt) plt.SetDocument(document);
            if (tool is TextTool tt) { tt.SetDocument(document); tt.SetColor(foregroundColor); }
            if (tool is ShapeTool st) { st.SetDocument(document); st.SetColor(foregroundColor); }
            if (tool is BlurBrushTool bbt) bbt.SetDocument(document);
            if (tool is SharpenBrushTool sbt) sbt.SetDocument(document);
            if (tool is SmudgeTool smt) smt.SetDocument(document);
            if (tool is HealingBrushTool hbt) hbt.SetDocument(document);

            // Show brush cursor for painting tools
            bool isBrushTool = tool is BrushTool or EraserTool or CloneStampTool or DodgeBurnTool
                or BlurBrushTool or SharpenBrushTool or SmudgeTool or HealingBrushTool;
            if (imageCanvas is not null)
            {
                imageCanvas.ShowBrushCursor = isBrushTool;
                imageCanvas.BrushCursorSize = isBrushTool ? toolService.Brush.Size : 0;
            }

            imageCanvas?.SetActiveTool(tool);
        }
    }

    // ═══════════════════════════════════════════════════
    //  Tool Service Wiring
    // ═══════════════════════════════════════════════════

    private void WireToolService()
    {
        toolService.ActiveToolChanged += _ =>
        {
            HighlightActiveTool();
            var def = toolService.GetActiveDefinition();
            ActiveToolLabel.Text = def.DisplayName;
            SyncActiveTool();
            PopulateOptionsBar();
        };
    }

    /// <summary>
    /// Populates the options bar with tool-specific controls based on the active tool.
    /// This is the Photoshop-style context-sensitive toolbar at the top.
    /// </summary>
    private void PopulateOptionsBar()
    {
        // Keep only the tool name label, remove any prior tool controls
        while (OptionsBarContent.Children.Count > 1)
            OptionsBarContent.Children.RemoveAt(OptionsBarContent.Children.Count - 1);

        // Add a separator after the tool name
        var sep = new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0) };
        sep[!Border.BackgroundProperty] = this.GetResourceObservable("SeparatorOpaqueBrush").ToBinding();
        OptionsBarContent.Children.Add(sep);

        var toolType = toolService.ActiveToolType;
        var brush = toolService.Brush;

        switch (toolType)
        {
            case EditorToolType.Brush:
            case EditorToolType.Eraser:
                AddOptionsBarSlider("Size:", 1, 500, brush.Size, "px", v => { brush.Size = v; if (imageCanvas is not null) imageCanvas.BrushCursorSize = v; });
                AddOptionsBarSlider("Hardness:", 0, 100, brush.Hardness * 100, "%", v => brush.Hardness = v / 100.0);
                AddOptionsBarSlider("Opacity:", 1, 100, brush.Opacity * 100, "%", v => brush.Opacity = v / 100.0);
                AddOptionsBarSlider("Flow:", 1, 100, brush.Flow * 100, "%", v => brush.Flow = v / 100.0);
                break;

            case EditorToolType.CloneStamp:
                AddOptionsBarSlider("Size:", 1, 500, brush.Size, "px", v => { brush.Size = v; if (imageCanvas is not null) imageCanvas.BrushCursorSize = v; });
                AddOptionsBarSlider("Hardness:", 0, 100, brush.Hardness * 100, "%", v => brush.Hardness = v / 100.0);
                AddOptionsBarSlider("Opacity:", 1, 100, brush.Opacity * 100, "%", v => brush.Opacity = v / 100.0);
                AddOptionsBarLabel("Alt+Click to set source");
                break;

            case EditorToolType.Dodge:
                if (toolInstances.TryGetValue(EditorToolType.Dodge, out var dbTool) && dbTool is DodgeBurnTool dbt)
                {
                    var modeCombo = new ComboBox
                    {
                        FontSize = 11, Height = 26, MinWidth = 90, VerticalAlignment = VerticalAlignment.Center,
                        ItemsSource = new[] { "Dodge", "Burn", "Sponge" },
                        SelectedIndex = (int)dbt.Mode,
                    };
                    modeCombo.SelectionChanged += (_, _) =>
                    {
                        if (modeCombo.SelectedIndex >= 0)
                            dbt.Mode = (DodgeBurnTool.ToolMode)modeCombo.SelectedIndex;
                    };
                    OptionsBarContent.Children.Add(modeCombo);
                    AddOptionsBarSlider("Exposure:", 1, 100, dbt.Exposure * 100, "%", v => dbt.Exposure = v / 100.0);
                    AddOptionsBarSlider("Size:", 1, 500, brush.Size, "px", v => { brush.Size = v; if (imageCanvas is not null) imageCanvas.BrushCursorSize = v; });
                }
                break;

            case EditorToolType.MagicWand:
                if (toolInstances.TryGetValue(EditorToolType.MagicWand, out var mwTool) && mwTool is MagicWandTool mwt)
                {
                    AddOptionsBarSlider("Tolerance:", 0, 255, mwt.Tolerance, "", v => mwt.Tolerance = (int)v);
                    var contiguousCheck = new CheckBox
                    {
                        Content = "Contiguous", IsChecked = mwt.Contiguous,
                        FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    };
                    contiguousCheck[!Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty] = this.GetResourceObservable("LabelPrimaryBrush").ToBinding();
                    contiguousCheck.IsCheckedChanged += (_, _) => mwt.Contiguous = contiguousCheck.IsChecked == true;
                    OptionsBarContent.Children.Add(contiguousCheck);
                }
                break;

            case EditorToolType.Shape:
                if (toolInstances.TryGetValue(EditorToolType.Shape, out var shTool) && shTool is ShapeTool st)
                {
                    var shapeCombo = new ComboBox
                    {
                        FontSize = 11, Height = 26, MinWidth = 100, VerticalAlignment = VerticalAlignment.Center,
                        ItemsSource = new[] { "Rectangle", "Ellipse", "Line" },
                        SelectedIndex = (int)st.ShapeType,
                    };
                    shapeCombo.SelectionChanged += (_, _) =>
                    {
                        if (shapeCombo.SelectedIndex >= 0)
                            st.ShapeType = (ShapeTool.ShapeKind)shapeCombo.SelectedIndex;
                    };
                    OptionsBarContent.Children.Add(shapeCombo);

                    var fillCheck = new CheckBox
                    {
                        Content = "Fill", IsChecked = st.Fill,
                        FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    };
                    fillCheck[!Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty] = this.GetResourceObservable("LabelPrimaryBrush").ToBinding();
                    fillCheck.IsCheckedChanged += (_, _) => st.Fill = fillCheck.IsChecked == true;
                    OptionsBarContent.Children.Add(fillCheck);

                    AddOptionsBarSlider("Stroke:", 1, 50, st.StrokeWidth, "px", v => st.StrokeWidth = (int)v);
                }
                break;

            case EditorToolType.Text:
                if (toolInstances.TryGetValue(EditorToolType.Text, out var txtTool) && txtTool is TextTool tt)
                {
                    // Collect all installed font family names
                    var fontNames = Avalonia.Media.FontManager.Current.SystemFonts
                        .Select(f => f.Name)
                        .OrderBy(n => n)
                        .ToList();

                    var fontCombo = new ComboBox
                    {
                        FontSize = 11, Height = 28, MinWidth = 180, MaxDropDownHeight = 400,
                        VerticalAlignment = VerticalAlignment.Center,
                        ItemsSource = fontNames,
                        SelectedItem = fontNames.Contains("Inter") ? "Inter" : fontNames.FirstOrDefault(),
                    };
                    fontCombo.SelectionChanged += (_, _) =>
                    {
                        if (fontCombo.SelectedItem is string selectedFont)
                            tt.FontFamilyName = selectedFont;
                    };
                    OptionsBarContent.Children.Add(fontCombo);

                    AddOptionsBarSlider("Size:", 6, 200, tt.FontSize, "px", v => tt.FontSize = (int)v);
                    AddOptionsBarLabel("Click to place, type text, Enter to commit");
                }
                break;

            case EditorToolType.Crop:
                {
                    var confirmBtn = new Button
                    {
                        Content = "Apply Crop",
                        Classes = { "accent" },
                        Height = 28,
                        FontSize = 12,
                        Padding = new Thickness(16, 0),
                        Margin = new Thickness(4, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center,
                    };
                    confirmBtn.Click += (_, _) =>
                    {
                        if (toolInstances.TryGetValue(EditorToolType.Crop, out var cropTool) && cropTool is CropTool ct)
                            ct.ApplyCrop();
                    };

                    var cancelBtn = new Button
                    {
                        Content = "Cancel Crop",
                        Height = 28,
                        FontSize = 12,
                        Padding = new Thickness(16, 0),
                        Margin = new Thickness(4, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center,
                    };
                    cancelBtn.Click += (_, _) =>
                    {
                        if (toolInstances.TryGetValue(EditorToolType.Crop, out var cropTool) && cropTool is CropTool ct)
                            ct.CancelCrop();
                    };

                    OptionsBarContent.Children.Add(confirmBtn);
                    OptionsBarContent.Children.Add(cancelBtn);
                    AddOptionsBarLabel("Draw crop region, then Apply or Cancel");
                }
                break;

            case EditorToolType.PaintBucket:
                AddOptionsBarSlider("Tolerance:", 0, 255, 32, "", _ => { });
                break;

            case EditorToolType.Gradient:
                AddOptionsBarLabel("Click and drag to define gradient direction");
                break;

            case EditorToolType.RectangularMarquee:
            case EditorToolType.EllipticalMarquee:
                AddOptionsBarLabel("Shift = constrain proportions");
                break;

            case EditorToolType.Lasso:
            case EditorToolType.PolygonalLasso:
                AddOptionsBarLabel("Draw selection path; release to close (Lasso) / double-click to close (Polygonal)");
                break;

            case EditorToolType.Move:
                AddOptionsBarLabel("Drag to move active layer");
                break;

            case EditorToolType.Eyedropper:
                AddOptionsBarLabel("Click to sample color from image");
                break;

            case EditorToolType.Zoom:
                AddOptionsBarLabel("Click to zoom in, Alt+Click to zoom out");
                break;

            case EditorToolType.Hand:
                AddOptionsBarLabel("Drag to pan the canvas");
                break;

            case EditorToolType.BlurBrush:
                if (toolInstances.TryGetValue(EditorToolType.BlurBrush, out var blurTool) && blurTool is BlurBrushTool blurBt)
                {
                    AddOptionsBarSlider("Size:", 1, 500, brush.Size, "px", v => { brush.Size = v; if (imageCanvas is not null) imageCanvas.BrushCursorSize = v; });
                    AddOptionsBarSlider("Strength:", 1, 100, blurBt.Strength * 100, "%", v => blurBt.Strength = v / 100.0);
                }
                break;

            case EditorToolType.SharpenBrush:
                if (toolInstances.TryGetValue(EditorToolType.SharpenBrush, out var sharpTool) && sharpTool is SharpenBrushTool sharpBt)
                {
                    AddOptionsBarSlider("Size:", 1, 500, brush.Size, "px", v => { brush.Size = v; if (imageCanvas is not null) imageCanvas.BrushCursorSize = v; });
                    AddOptionsBarSlider("Strength:", 1, 100, sharpBt.Strength * 100, "%", v => sharpBt.Strength = v / 100.0);
                }
                break;

            case EditorToolType.Smudge:
                if (toolInstances.TryGetValue(EditorToolType.Smudge, out var smudgeTl) && smudgeTl is SmudgeTool smudgeT)
                {
                    AddOptionsBarSlider("Size:", 1, 500, brush.Size, "px", v => { brush.Size = v; if (imageCanvas is not null) imageCanvas.BrushCursorSize = v; });
                    AddOptionsBarSlider("Strength:", 1, 100, smudgeT.Strength * 100, "%", v => smudgeT.Strength = v / 100.0);
                }
                break;

            case EditorToolType.HealingBrush:
                AddOptionsBarSlider("Size:", 1, 500, brush.Size, "px", v => { brush.Size = v; if (imageCanvas is not null) imageCanvas.BrushCursorSize = v; });
                AddOptionsBarSlider("Hardness:", 0, 100, brush.Hardness * 100, "%", v => brush.Hardness = v / 100.0);
                AddOptionsBarSlider("Opacity:", 1, 100, brush.Opacity * 100, "%", v => brush.Opacity = v / 100.0);
                AddOptionsBarLabel("Alt+Click to set source");
                break;

            case EditorToolType.Measure:
                AddOptionsBarLabel("Click and drag to measure distance and angle. Escape to clear.");
                break;

            case EditorToolType.PenTool:
                AddOptionsBarLabel("Click to add point, drag for curves. Double-click or click first point to close. Backspace to undo last point.");
                break;
        }
    }

    private void AddOptionsBarSlider(string label, double min, double max, double value, string unit, Action<double> onChanged)
    {
        var lbl = new TextBlock
        {
            Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 2, 0),
        };
        lbl[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelSecondaryBrush").ToBinding();

        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value, Width = 100,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var valText = new TextBlock
        {
            Text = $"{value:F0}{unit}", FontSize = 11, Width = 36,
            VerticalAlignment = VerticalAlignment.Center,
        };
        valText[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelTertiaryBrush").ToBinding();

        slider.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Value" && s is Slider sl)
            {
                valText.Text = $"{sl.Value:F0}{unit}";
                onChanged(sl.Value);
            }
        };

        OptionsBarContent.Children.Add(lbl);
        OptionsBarContent.Children.Add(slider);
        OptionsBarContent.Children.Add(valText);
    }

    private void AddOptionsBarLabel(string text)
    {
        var lbl = new TextBlock
        {
            Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0),
        };
        lbl[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelTertiaryBrush").ToBinding();
        OptionsBarContent.Children.Add(lbl);
    }

    // ═══════════════════════════════════════════════════
    //  Undo Service Wiring
    // ═══════════════════════════════════════════════════

    private void WireUndoService()
    {
        undoService.StateChanged += () =>
        {
            UpdateUndoMenuState();
            RefreshHistoryPanel();
            RefreshLayersPanel();
            RefreshNavigatorPreview();
        };
    }

    // ═══════════════════════════════════════════════════
    //  Canvas
    // ═══════════════════════════════════════════════════

    private void BuildCanvas()
    {
        imageCanvas = new ImageCanvas();
        imageCanvas.ClipToBounds = true; // Ensure rendering is clipped to bounds

        imageCanvas.CursorPositionChanged += (x, y) =>
        {
            StatusCursorPos.Text = $"{x}, {y}";
        };

        imageCanvas.ZoomChanged += z =>
        {
            StatusZoom.Text = $"{z * 100:F0}%";
            if (navigatorZoomSlider is not null)
                navigatorZoomSlider.Value = z * 100;
        };

        // Add to CanvasHost panel (behind the empty message)
        CanvasHost.Children.Insert(0, imageCanvas);
    }

    // ═══════════════════════════════════════════════════
    //  Panel Dock (right side collapsible panels)
    // ═══════════════════════════════════════════════════

    private void BuildPanelDock()
    {
        // Navigator
        navigatorPanel = new CollapsiblePanel("Navigator", "IconNavigation",
            BuildNavigatorContent());

        // Color
        colorPanel = new CollapsiblePanel("Color", "IconPalette",
            BuildColorContent());

        // Layers (manages own scroll so action bar stays pinned at bottom)
        layersPanel = new CollapsiblePanel("Layers", "IconLayers",
            BuildLayersContent(), managesOwnScroll: true);

        // History
        historyPanel = new CollapsiblePanel("History", "IconHistory",
            BuildHistoryContent());

        // Properties (hidden by default)
        propertiesPanel = new CollapsiblePanel("Properties", "IconSliders",
            BuildPropertiesContent());

        // Channels (hidden by default)
        channelsPanel = new CollapsiblePanel("Channels", "IconGrid",
            BuildChannelsContent());

        // Brushes (hidden by default)
        brushesPanel = new CollapsiblePanel("Brushes", "IconPaintbrush",
            BuildBrushesContent());

        // Adjustments (hidden by default)
        adjustmentsPanel = new CollapsiblePanel("Adjustments", "IconSparkles",
            BuildAdjustmentsContent());

        PanelGrid.Children.Add(navigatorPanel);
        PanelGrid.Children.Add(colorPanel);
        PanelGrid.Children.Add(layersPanel);
        PanelGrid.Children.Add(historyPanel);
        PanelGrid.Children.Add(propertiesPanel);
        PanelGrid.Children.Add(channelsPanel);
        PanelGrid.Children.Add(brushesPanel);
        PanelGrid.Children.Add(adjustmentsPanel);

        // Wire expand/collapse changes to trigger layout recalc
        foreach (var panel in new[] { navigatorPanel, colorPanel, layersPanel, historyPanel,
            propertiesPanel, channelsPanel, brushesPanel, adjustmentsPanel })
        {
            panel.ExpandedChanged += UpdatePanelGridLayout;
        }

        SyncPanelVisibility();
    }    private Control BuildNavigatorContent()
    {
        var previewContainer = new Border
        {
            Height = 140,
            Margin = new Thickness(8, 4),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
        };
        previewContainer[!Border.BackgroundProperty] = this.GetResourceObservable("BgCanvasBrush").ToBinding();

        navigatorPreviewImage = new Avalonia.Controls.Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        previewContainer.Child = navigatorPreviewImage;

        var zoomRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            Margin = new Thickness(8, 4, 8, 8),
        };

        var zoomOutBtn = new Button { Classes = { "toolbar" }, Width = 24, Height = 24,
            Content = LucideIcon.CreateFromResource(this, "IconZoomOut", 12) };
        var zoomSlider = new Slider { Minimum = 1, Maximum = 6400, Value = 100,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
        var zoomInBtn = new Button { Classes = { "toolbar" }, Width = 24, Height = 24,
            Content = LucideIcon.CreateFromResource(this, "IconZoomIn", 12) };

        // Wire zoom controls to canvas
        zoomOutBtn.Click += (_, _) => imageCanvas?.ZoomOut();
        zoomInBtn.Click += (_, _) => imageCanvas?.ZoomIn();
        navigatorZoomSlider = zoomSlider;
        zoomSlider.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Value" && s is Slider sl && imageCanvas is not null)
            {
                imageCanvas.SetZoom(sl.Value / 100.0);
            }
        };

        Grid.SetColumn(zoomOutBtn, 0);
        Grid.SetColumn(zoomSlider, 1);
        Grid.SetColumn(zoomInBtn, 2);
        zoomRow.Children.Add(zoomOutBtn);
        zoomRow.Children.Add(zoomSlider);
        zoomRow.Children.Add(zoomInBtn);

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(previewContainer);
        stack.Children.Add(zoomRow);
        return stack;
    }

    private Control BuildColorContent()
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4, 8, 8) };

        // Hue strip (clickable to change hue)
        var hueStrip = new Border
        {
            Height = 20,
            CornerRadius = new CornerRadius(3),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.Red, 0),
                    new GradientStop(Colors.Yellow, 0.17),
                    new GradientStop(Colors.Lime, 0.33),
                    new GradientStop(Colors.Cyan, 0.5),
                    new GradientStop(Colors.Blue, 0.67),
                    new GradientStop(Colors.Magenta, 0.83),
                    new GradientStop(Colors.Red, 1),
                },
            },
        };
        hueStrip.PointerPressed += (_, e) =>
        {
            var pos = e.GetPosition(hueStrip);
            double hue = (pos.X / hueStrip.Bounds.Width) * 360.0;
            hue = Math.Clamp(hue, 0, 360);
            foregroundColor = HsvToColor(hue, 1.0, 1.0);
            colorSwatch?.SetColors(foregroundColor, backgroundColor);
            UpdateColorFields();
        };

        // RGB value row
        var rgbRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,8,Auto,*,8,Auto,*"),
            Height = 24,
        };

        colorRedField = CreateColorField("0");
        colorGreenField = CreateColorField("0");
        colorBlueField = CreateColorField("0");

        AddLabelAndFieldDirect(rgbRow, "R", 0, colorRedField, 1);
        AddLabelAndFieldDirect(rgbRow, "G", 3, colorGreenField, 4);
        AddLabelAndFieldDirect(rgbRow, "B", 6, colorBlueField, 7);

        // Wire RGB field changes to update foreground color
        colorRedField.LostFocus += (_, _) => ApplyColorFromFields();
        colorGreenField.LostFocus += (_, _) => ApplyColorFromFields();
        colorBlueField.LostFocus += (_, _) => ApplyColorFromFields();

        // Hex value
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var hexLabel = new TextBlock { Text = "#", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        hexLabel[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelSecondaryBrush").ToBinding();
        colorHexField = new TextBox { Text = "000000", Width = 70, FontSize = 11, Height = 24, Padding = new Thickness(4, 2) };
        colorHexField.LostFocus += (_, _) => ApplyColorFromHex();
        hexRow.Children.Add(hexLabel);
        hexRow.Children.Add(colorHexField);

        stack.Children.Add(hueStrip);
        stack.Children.Add(rgbRow);
        stack.Children.Add(hexRow);

        UpdateColorFields();
        return stack;
    }

    private TextBox CreateColorField(string initial) =>
        new() { Text = initial, Width = 40, FontSize = 11, Height = 24, Padding = new Thickness(4, 2) };

    private void AddLabelAndFieldDirect(Grid grid, string label, int labelCol, TextBox field, int fieldCol)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, labelCol);
        Grid.SetColumn(field, fieldCol);
        grid.Children.Add(lbl);
        grid.Children.Add(field);
    }

    private void UpdateColorFields()
    {
        if (colorRedField is not null) colorRedField.Text = foregroundColor.R.ToString();
        if (colorGreenField is not null) colorGreenField.Text = foregroundColor.G.ToString();
        if (colorBlueField is not null) colorBlueField.Text = foregroundColor.B.ToString();
        if (colorHexField is not null) colorHexField.Text = $"{foregroundColor.R:X2}{foregroundColor.G:X2}{foregroundColor.B:X2}";
        PropagateColorToTools();
    }

    /// <summary>Pushes the current foreground/background colors to all color-sensitive tools.</summary>
    private void PropagateColorToTools()
    {
        foreach (var (_, tool) in toolInstances)
        {
            if (tool is BrushTool bt) bt.SetColor(foregroundColor);
            else if (tool is EraserTool) { } // eraser uses transparency
            else if (tool is PaintBucketTool pbt) pbt.SetColor(foregroundColor);
            else if (tool is GradientTool gt) gt.SetColors(foregroundColor, backgroundColor);
            else if (tool is TextTool tt) tt.SetColor(foregroundColor);
            else if (tool is ShapeTool st) st.SetColor(foregroundColor);
        }
    }

    private void ApplyColorFromFields()
    {
        if (byte.TryParse(colorRedField?.Text, out byte r) &&
            byte.TryParse(colorGreenField?.Text, out byte g) &&
            byte.TryParse(colorBlueField?.Text, out byte b))
        {
            foregroundColor = Color.FromArgb(255, r, g, b);
            colorSwatch?.SetColors(foregroundColor, backgroundColor);
            if (colorHexField is not null) colorHexField.Text = $"{r:X2}{g:X2}{b:X2}";
            PropagateColorToTools();
        }
    }

    private void ApplyColorFromHex()
    {
        var hex = colorHexField?.Text?.Trim() ?? "";
        if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint val))
        {
            byte r = (byte)((val >> 16) & 0xFF);
            byte g = (byte)((val >> 8) & 0xFF);
            byte b = (byte)(val & 0xFF);
            foregroundColor = Color.FromArgb(255, r, g, b);
            colorSwatch?.SetColors(foregroundColor, backgroundColor);
            if (colorRedField is not null) colorRedField.Text = r.ToString();
            if (colorGreenField is not null) colorGreenField.Text = g.ToString();
            if (colorBlueField is not null) colorBlueField.Text = b.ToString();
            PropagateColorToTools();
        }
    }

    private static Color HsvToColor(double hue, double sat, double val)
    {
        double c = val * sat;
        double x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        double m = val - c;
        double r, g, b;
        if (hue < 60)       { r = c; g = x; b = 0; }
        else if (hue < 120) { r = x; g = c; b = 0; }
        else if (hue < 180) { r = 0; g = c; b = x; }
        else if (hue < 240) { r = 0; g = x; b = c; }
        else if (hue < 300) { r = x; g = 0; b = c; }
        else                { r = c; g = 0; b = x; }
        return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private Control BuildLayersContent()
    {
        // Grid: blend/opacity (Auto) | scrollable layer list (*) | action bar (Auto)
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        // Blend mode + opacity row
        var topRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,8,Auto"),
            Margin = new Thickness(8, 4),
        };

        layerBlendCombo = new ComboBox
        {
            FontSize = 11,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Normal", "Multiply", "Screen", "Overlay", "Darken", "Lighten",
                "Color Dodge", "Color Burn", "Hard Light", "Soft Light", "Difference", "Exclusion" },
            SelectedIndex = 0,
        };
        layerBlendCombo.SelectionChanged += (_, _) => ApplyLayerBlendMode();
        var opacityRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var opacityLabel = new TextBlock { Text = "Opacity:", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        opacityLabel[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelSecondaryBrush").ToBinding();
        layerOpacityField = new TextBox { Text = "100%", Width = 48, FontSize = 10, Height = 22, Padding = new Thickness(3, 1) };
        layerOpacityField.LostFocus += (_, _) => ApplyLayerOpacity();
        opacityRow.Children.Add(opacityLabel);
        opacityRow.Children.Add(layerOpacityField);

        Grid.SetColumn(layerBlendCombo, 0);
        Grid.SetColumn(opacityRow, 2);
        topRow.Children.Add(layerBlendCombo);
        topRow.Children.Add(opacityRow);

        // Dynamic layer list inside its own ScrollViewer
        layerListPanel = new StackPanel { Spacing = 1, Margin = new Thickness(4, 4) };
        var layerScroll = new ScrollViewer
        {
            Content = layerListPanel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        // Bottom action bar with wired buttons
        var actionBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4),
        };

        var addBtn = MakePanelActionButton("IconPlus", "New Layer");
        addBtn.Click += (_, _) => OnNewLayer();
        var dupeBtn = MakePanelActionButton("IconCopy", "Duplicate Layer");
        dupeBtn.Click += (_, _) => OnDuplicateLayer();
        var moveDownBtn = MakePanelActionButton("IconChevronDown", "Move Down");
        moveDownBtn.Click += (_, _) => OnMoveLayerDown();
        var moveUpBtn = MakePanelActionButton("IconChevronUp", "Move Up");
        moveUpBtn.Click += (_, _) => OnMoveLayerUp();
        var deleteBtn = MakePanelActionButton("IconTrash", "Delete Layer");
        deleteBtn.Click += (_, _) => OnDeleteLayer();

        actionBar.Children.Add(addBtn);
        actionBar.Children.Add(dupeBtn);
        actionBar.Children.Add(MakePanelActionButton("IconFolder", "Group"));
        actionBar.Children.Add(moveDownBtn);
        actionBar.Children.Add(moveUpBtn);
        actionBar.Children.Add(deleteBtn);

        Grid.SetRow(topRow, 0);
        Grid.SetRow(layerScroll, 1);
        Grid.SetRow(actionBar, 2);
        grid.Children.Add(topRow);
        grid.Children.Add(layerScroll);
        grid.Children.Add(actionBar);

        RefreshLayersPanel();
        return grid;
    }

    private void OnMoveLayerUp()
    {
        if (document is null || document.ActiveLayerIndex >= document.Layers.Count - 1) return;
        int from = document.ActiveLayerIndex;
        int to = from + 1;
        document.Layers.MoveLayer(from, to);
        document.ActiveLayerIndex = to;
        document.IsDirty = true;
        undoService.Push(new MoveLayerCommand("Move Layer Up", from, to));
        imageCanvas?.MarkDirty();
        RefreshLayersPanel();
    }

    private void OnMoveLayerDown()
    {
        if (document is null || document.ActiveLayerIndex <= 0) return;
        int from = document.ActiveLayerIndex;
        int to = from - 1;
        document.Layers.MoveLayer(from, to);
        document.ActiveLayerIndex = to;
        document.IsDirty = true;
        undoService.Push(new MoveLayerCommand("Move Layer Down", from, to));
        imageCanvas?.MarkDirty();
        RefreshLayersPanel();
    }

    private void RefreshLayersPanel()
    {
        if (layerListPanel is null) return;
        layerListPanel.Children.Clear();

        if (document is null)
        {
            var emptyMsg = new TextBlock
            {
                Text = "No document",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12),
            };
            emptyMsg[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelTertiaryBrush").ToBinding();
            layerListPanel.Children.Add(emptyMsg);
            return;
        }

        // Display layers in reverse order (top layer first, like Photoshop)
        for (int i = document.Layers.Count - 1; i >= 0; i--)
        {
            var layer = document.Layers[i];
            bool isActive = i == document.ActiveLayerIndex;
            int capturedIndex = i;

            var row = BuildLayerRow(layer.Name, layer.Visible, isActive, capturedIndex);
            layerListPanel.Children.Add(row);
        }

        // Update blend mode and opacity for active layer
        if (document.ActiveLayerIndex < document.Layers.Count)
        {
            var activeLayer = document.Layers[document.ActiveLayerIndex];
            if (layerOpacityField is not null)
                layerOpacityField.Text = $"{(int)(activeLayer.Opacity * 100)}%";
        }

        RefreshPropertiesPanel();
    }

    private void ApplyLayerOpacity()
    {
        if (document is null || layerOpacityField is null) return;
        var text = layerOpacityField.Text?.Replace("%", "").Trim() ?? "";
        if (int.TryParse(text, out int pct))
        {
            pct = Math.Clamp(pct, 0, 100);
            var layer = document.Layers[document.ActiveLayerIndex];
            double oldOpacity = layer.Opacity;
            double newOpacity = pct / 100.0;
            layer.Opacity = newOpacity;
            document.IsDirty = true;

            int idx = document.ActiveLayerIndex;
            undoService.Push(new LayerPropertyCommand(
                $"Opacity {pct}%", idx,
                l => l.Opacity = oldOpacity,
                l => l.Opacity = newOpacity));
            imageCanvas?.MarkDirty();
            layerOpacityField.Text = $"{pct}%";
        }
    }

    private static readonly Dictionary<string, SharpImage.Transform.CompositeMode> BlendModeMap = new()
    {
        ["Normal"] = SharpImage.Transform.CompositeMode.Over,
        ["Multiply"] = SharpImage.Transform.CompositeMode.Multiply,
        ["Screen"] = SharpImage.Transform.CompositeMode.Screen,
        ["Overlay"] = SharpImage.Transform.CompositeMode.Overlay,
        ["Darken"] = SharpImage.Transform.CompositeMode.Darken,
        ["Lighten"] = SharpImage.Transform.CompositeMode.Lighten,
        ["Color Dodge"] = SharpImage.Transform.CompositeMode.ColorDodge,
        ["Color Burn"] = SharpImage.Transform.CompositeMode.ColorBurn,
        ["Hard Light"] = SharpImage.Transform.CompositeMode.HardLight,
        ["Soft Light"] = SharpImage.Transform.CompositeMode.SoftLight,
        ["Difference"] = SharpImage.Transform.CompositeMode.Subtract,
        ["Exclusion"] = SharpImage.Transform.CompositeMode.Subtract,
    };

    private void ApplyLayerBlendMode()
    {
        if (document is null || layerBlendCombo is null) return;
        var selected = layerBlendCombo.SelectedItem?.ToString();
        if (selected is null || !BlendModeMap.TryGetValue(selected, out var mode)) return;

        var layer = document.Layers[document.ActiveLayerIndex];
        var oldMode = layer.BlendMode;
        layer.BlendMode = mode;
        document.IsDirty = true;

        int idx = document.ActiveLayerIndex;
        undoService.Push(new LayerPropertyCommand(
            $"Blend: {selected}", idx,
            l => l.BlendMode = oldMode,
            l => l.BlendMode = mode));
        imageCanvas?.MarkDirty();
    }

    private Border BuildLayerRow(string layerName, bool isVisible, bool isSelected, int layerIndex)
    {
        var row = new Border
        {
            Height = 40,
            Padding = new Thickness(4, 2),
            CornerRadius = new CornerRadius(4),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        if (isSelected)
            row[!Border.BackgroundProperty] = this.GetResourceObservable("FillSecondaryBrush").ToBinding();

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,8,Auto,*"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Visibility toggle
        var eyeBtn = new Button
        {
            Classes = { "toolbar" },
            Width = 24, Height = 24,
            Content = LucideIcon.CreateFromResource(this, isVisible ? "IconEye" : "IconEyeOff", 14),
        };
        eyeBtn.Click += (_, _) =>
        {
            if (document is null || layerIndex >= document.Layers.Count) return;
            var layer = document.Layers[layerIndex];
            bool oldVisible = layer.Visible;
            layer.Visible = !oldVisible;
            document.IsDirty = true;
            undoService.Push(new LayerPropertyCommand(
                oldVisible ? "Hide Layer" : "Show Layer", layerIndex,
                l => l.Visible = oldVisible,
                l => l.Visible = !oldVisible));
            imageCanvas?.MarkDirty();
            RefreshLayersPanel();
        };

        var thumb = new Border
        {
            Width = 32, Height = 32,
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
        };
        thumb[!Border.BorderBrushProperty] = this.GetResourceObservable("SeparatorOpaqueBrush").ToBinding();
        thumb.BorderThickness = new Thickness(1);
        thumb[!Border.BackgroundProperty] = this.GetResourceObservable("BgCanvasBrush").ToBinding();

        var nameBlock = new TextBlock
        {
            Text = layerName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
            Opacity = isVisible ? 1.0 : 0.4,
        };
        nameBlock[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelPrimaryBrush").ToBinding();

        Grid.SetColumn(eyeBtn, 0);
        Grid.SetColumn(thumb, 2);
        Grid.SetColumn(nameBlock, 3);
        grid.Children.Add(eyeBtn);
        grid.Children.Add(thumb);
        grid.Children.Add(nameBlock);

        row.Child = grid;

        // Click to select this layer
        row.PointerPressed += (_, _) =>
        {
            if (document is null || layerIndex >= document.Layers.Count) return;
            document.ActiveLayerIndex = layerIndex;
            SyncActiveTool();
            RefreshLayersPanel();
        };

        return row;
    }

    private Control BuildHistoryContent()
    {
        historyList = new StackPanel { Spacing = 1, Margin = new Thickness(4, 4, 4, 8), MinHeight = 60 };
        RefreshHistoryPanel();
        // No inner ScrollViewer — CollapsiblePanel provides its own
        return historyList;
    }

    private void RefreshHistoryPanel()
    {
        if (historyList is null) return;
        historyList.Children.Clear();

        if (undoService.UndoCount == 0)
        {
            var emptyMsg = new TextBlock
            {
                Text = "No history",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12),
            };
            emptyMsg[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelTertiaryBrush").ToBinding();
            historyList.Children.Add(emptyMsg);
            return;
        }

        var history = undoService.History;
        for (int i = 0; i < history.Count; i++)
        {
            var cmd = history[i];
            int capturedIndex = i;

            var row = new Border
            {
                Height = 26,
                Padding = new Thickness(8, 2),
                CornerRadius = new CornerRadius(3),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };

            // Highlight the current (last) undo state
            if (i == history.Count - 1)
                row[!Border.BackgroundProperty] = this.GetResourceObservable("AccentBlueBrush").ToBinding();

            var label = new TextBlock
            {
                Text = cmd.Description,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label[!TextBlock.ForegroundProperty] = this.GetResourceObservable(
                i == history.Count - 1 ? "LabelPrimaryBrush" : "LabelSecondaryBrush").ToBinding();

            row.Child = label;
            row.PointerPressed += (_, _) =>
            {
                if (document is not null)
                {
                    undoService.JumpToState(capturedIndex, document);
                    imageCanvas?.MarkDirty();
                    RefreshHistoryPanel();
                    RefreshLayersPanel();
                    UpdateStatusBar();
                    UpdateTitle();
                }
            };
            historyList.Children.Add(row);
        }

        // Auto-scroll to show the most recent history item
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            historyPanel?.ContentScrollViewer?.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Updates the navigator panel thumbnail with a scaled preview of the current document.
    /// </summary>
    private void RefreshNavigatorPreview()
    {
        if (navigatorPreviewImage is null) return;

        if (document is null || document.Layers.Count == 0)
        {
            navigatorPreviewImage.Source = null;
            return;
        }

        try
        {
            var flattened = document.Flatten();
            int srcW = (int)flattened.Columns;
            int srcH = (int)flattened.Rows;
            if (srcW == 0 || srcH == 0) return;

            // Scale to fit 220x130 preview
            const int maxW = 220, maxH = 130;
            double scale = Math.Min((double)maxW / srcW, (double)maxH / srcH);
            int thumbW = Math.Max(1, (int)(srcW * scale));
            int thumbH = Math.Max(1, (int)(srcH * scale));

            // Resize using nearest-neighbor for speed
            var resized = SharpImage.Transform.Resize.Apply(flattened, thumbW, thumbH,
                SharpImage.Transform.InterpolationMethod.Bilinear);

            // Create or reuse WriteableBitmap
            var size = new Avalonia.PixelSize(thumbW, thumbH);
            if (navigatorBitmap is null || navigatorBitmap.PixelSize != size)
                navigatorBitmap = new Avalonia.Media.Imaging.WriteableBitmap(size, new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

            using var fb = navigatorBitmap.Lock();
            int channels = resized.NumberOfChannels;
            bool hasAlpha = resized.HasAlpha;

            unsafe
            {
                byte* dst = (byte*)fb.Address;
                for (int y = 0; y < thumbH; y++)
                {
                    var row = resized.GetPixelRow(y);
                    for (int x = 0; x < thumbW; x++)
                    {
                        int srcIdx = x * channels;
                        byte r = 0, g = 0, b = 0, a = 255;

                        if (channels >= 3)
                        {
                            r = (byte)(row[srcIdx] * 255.0 / 65535.0 + 0.5);
                            g = (byte)(row[srcIdx + 1] * 255.0 / 65535.0 + 0.5);
                            b = (byte)(row[srcIdx + 2] * 255.0 / 65535.0 + 0.5);
                            if (hasAlpha && channels >= 4)
                                a = (byte)(row[srcIdx + 3] * 255.0 / 65535.0 + 0.5);
                        }
                        else if (channels == 1)
                        {
                            r = g = b = (byte)(row[srcIdx] * 255.0 / 65535.0 + 0.5);
                        }

                        // Premultiply alpha for Avalonia
                        int dstIdx = (y * fb.RowBytes) + x * 4;
                        dst[dstIdx + 0] = (byte)(b * a / 255);
                        dst[dstIdx + 1] = (byte)(g * a / 255);
                        dst[dstIdx + 2] = (byte)(r * a / 255);
                        dst[dstIdx + 3] = a;
                    }
                }
            }

            navigatorPreviewImage.Source = navigatorBitmap;
        }
        catch
        {
            // Silently ignore preview failures
        }
    }

    private Control BuildPropertiesContent()
    {
        propertiesContent = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4, 8, 8), MinHeight = 80 };
        RefreshPropertiesPanel();
        return propertiesContent;
    }

    private void RefreshPropertiesPanel()
    {
        if (propertiesContent is null) return;
        propertiesContent.Children.Clear();

        if (document is null || document.Layers.Count == 0)
        {
            var msg = new TextBlock
            {
                Text = "No document open",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            };
            msg[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelTertiaryBrush").ToBinding();
            propertiesContent.Children.Add(msg);
            return;
        }

        var layer = document.Layers[document.ActiveLayerIndex];
        string layerName = $"Layer {document.ActiveLayerIndex + 1}";

        AddPropertyRow("Layer", layerName);
        if (layer.Content is not null)
        {
            AddPropertyRow("Size", $"{layer.Content.Columns} × {layer.Content.Rows}");
            AddPropertyRow("Channels", $"{layer.Content.NumberOfChannels} ({(layer.Content.HasAlpha ? "RGBA" : "RGB")})");
        }
        AddPropertyRow("Visible", layer.Visible ? "Yes" : "No");
        AddPropertyRow("Opacity", $"{(int)(layer.Opacity * 100)}%");

        // Find blend mode display name
        string blendName = "Normal";
        foreach (var kv in BlendModeMap)
        {
            if (kv.Value == layer.BlendMode) { blendName = kv.Key; break; }
        }
        AddPropertyRow("Blend", blendName);
        AddPropertyRow("Offset", $"({layer.OffsetX}, {layer.OffsetY})");
        AddPropertyRow("Document", $"{document.Width} × {document.Height}");
    }

    private void AddPropertyRow(string label, string value)
    {
        if (propertiesContent is null) return;
        var row = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("70,*"),
            Height = 20,
        };
        var lbl = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        lbl[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelSecondaryBrush").ToBinding();
        var val = new TextBlock { Text = value, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        val[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelPrimaryBrush").ToBinding();
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(val, 1);
        row.Children.Add(lbl);
        row.Children.Add(val);
        propertiesContent.Children.Add(row);
    }

    private Control BuildChannelsContent()
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(4, 4, 4, 8) };

        string[] channels = ["Red", "Green", "Blue", "Alpha"];
        bool[] states = [true, true, true, true];

        for (int i = 0; i < channels.Length; i++)
        {
            int channelIdx = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Height = 28 };
            var eye = new Button { Classes = { "toolbar" }, Width = 24, Height = 24,
                Content = LucideIcon.CreateFromResource(this, "IconEye", 14) };
            var label = new TextBlock { Text = channels[i], FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelPrimaryBrush").ToBinding();

            eye.Click += (sender, _) =>
            {
                if (imageCanvas is null) return;
                states[channelIdx] = !states[channelIdx];
                var btn = (Button)sender!;
                btn.Content = LucideIcon.CreateFromResource(this,
                    states[channelIdx] ? "IconEye" : "IconEyeOff", 14);
                btn.Opacity = states[channelIdx] ? 1.0 : 0.4;

                switch (channelIdx)
                {
                    case 0: imageCanvas.ShowRedChannel = states[0]; break;
                    case 1: imageCanvas.ShowGreenChannel = states[1]; break;
                    case 2: imageCanvas.ShowBlueChannel = states[2]; break;
                    case 3: imageCanvas.ShowAlphaChannel = states[3]; break;
                }
                imageCanvas.MarkDirty();
            };

            row.Children.Add(eye);
            row.Children.Add(label);
            stack.Children.Add(row);
        }
        return stack;
    }

    private Control BuildBrushesContent()
    {
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(8, 4, 8, 8) };
        var brush = toolService.Brush;

        stack.Children.Add(BuildWiredSliderRow("Size", 1, 500, brush.Size, "px",
            v => { brush.Size = v; if (imageCanvas is not null) imageCanvas.BrushCursorSize = v; }));
        stack.Children.Add(BuildWiredSliderRow("Hardness", 0, 100, brush.Hardness * 100, "%",
            v => brush.Hardness = v / 100.0));
        stack.Children.Add(BuildWiredSliderRow("Opacity", 0, 100, brush.Opacity * 100, "%",
            v => brush.Opacity = v / 100.0));
        stack.Children.Add(BuildWiredSliderRow("Flow", 0, 100, brush.Flow * 100, "%",
            v => brush.Flow = v / 100.0));
        stack.Children.Add(BuildWiredSliderRow("Spacing", 1, 200, brush.Spacing * 100, "%",
            v => brush.Spacing = v / 100.0));

        return stack;
    }

    private Control BuildWiredSliderRow(string label, double min, double max, double value, string unit, Action<double> onChanged)
    {
        var row = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("60,*,40"),
            Height = 22,
        };

        var lbl = new TextBlock { Text = label, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        lbl[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelSecondaryBrush").ToBinding();

        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
        };

        var valText = new TextBlock
        {
            Text = $"{value:F0}{unit}",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        valText[!TextBlock.ForegroundProperty] = this.GetResourceObservable("LabelTertiaryBrush").ToBinding();

        slider.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Value" && s is Slider sl)
            {
                valText.Text = $"{sl.Value:F0}{unit}";
                onChanged(sl.Value);
            }
        };

        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valText, 2);
        row.Children.Add(lbl);
        row.Children.Add(slider);
        row.Children.Add(valText);
        return row;
    }

    private Control BuildAdjustmentsContent()
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4, 8, 8) };

        (string name, Action handler)[] adjustments =
        [
            ("Brightness/Contrast", OpenBrightnessContrast),
            ("Levels", OpenLevels),
            ("Curves", OpenCurves),
            ("Hue/Saturation", OpenHueSaturation),
            ("Color Balance", OpenColorBalance),
            ("Vibrance", OpenVibrance),
            ("Exposure", OpenExposure),
            ("Posterize", OpenPosterize),
            ("Threshold", OpenThreshold),
            ("Invert", () => { if (document is not null) ApplyInstantFilter("Invert", () => SharpImage.Effects.ColorAdjust.Invert(GetFilterSource())); }),
        ];

        foreach (var (name, handler) in adjustments)
        {
            var btn = new Button
            {
                Classes = { "effect" },
                Content = name,
            };
            btn.Click += (_, _) => { if (document is not null) handler(); };
            stack.Children.Add(btn);
        }

        return stack;
    }

    private Button MakePanelActionButton(string iconKey, string tooltip)
    {
        var btn = new Button
        {
            Classes = { "toolbar" },
            Width = 28,
            Height = 28,
            Content = LucideIcon.CreateFromResource(this, iconKey, 14),
        };
        ToolTip.SetTip(btn, tooltip);
        return btn;
    }

    private void SyncPanelVisibility()
    {
        if (navigatorPanel is not null) navigatorPanel.IsVisible = workspaceService.ShowNavigator;
        if (colorPanel is not null) colorPanel.IsVisible = workspaceService.ShowColor;
        if (layersPanel is not null) layersPanel.IsVisible = workspaceService.ShowLayers;
        if (historyPanel is not null) historyPanel.IsVisible = workspaceService.ShowHistory;
        if (propertiesPanel is not null) propertiesPanel.IsVisible = workspaceService.ShowProperties;
        if (channelsPanel is not null) channelsPanel.IsVisible = workspaceService.ShowChannels;
        if (brushesPanel is not null) brushesPanel.IsVisible = workspaceService.ShowBrushes;
        if (adjustmentsPanel is not null) adjustmentsPanel.IsVisible = workspaceService.ShowAdjustments;
        UpdatePanelGridLayout();
    }

    /// <summary>
    /// Recalculates Grid row definitions so visible+expanded panels share space equally
    /// and collapsed/hidden panels take only their header height.
    /// </summary>
    private void UpdatePanelGridLayout()
    {
        if (PanelGrid is null) return;

        PanelGrid.RowDefinitions.Clear();
        int rowIndex = 0;
        foreach (var child in PanelGrid.Children)
        {
            if (child is not CollapsiblePanel panel) continue;

            bool visibleAndExpanded = panel.IsVisible && panel.IsExpanded;
            var rowDef = new RowDefinition(visibleAndExpanded ? GridLength.Star : GridLength.Auto);
            PanelGrid.RowDefinitions.Add(rowDef);
            Grid.SetRow(panel, rowIndex);
            rowIndex++;
        }
    }

    private void WireWorkspaceService()
    {
        workspaceService.LayoutChanged += SyncPanelVisibility;
    }

    // ═══════════════════════════════════════════════════
    //  Keyboard Shortcuts
    // ═══════════════════════════════════════════════════

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Space key: temporary hand tool
        if (e.Key == Key.Space && !isSpaceHeld && !IsTextInputFocused())
        {
            isSpaceHeld = true;
            toolBeforeSpaceHand = toolService.ActiveToolType;
            toolService.ActiveToolType = EditorToolType.Hand;
            e.Handled = true;
            return;
        }

        // Bracket keys work with Shift modifier for hardness
        if (!IsTextInputFocused())
        {
            bool hasShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (e.Key == Key.OemOpenBrackets) // [
            {
                if (hasShift)
                    toolService.Brush.Hardness = Math.Max(0, toolService.Brush.Hardness - 0.1);
                else
                    toolService.Brush.Size = Math.Max(1, toolService.Brush.Size - 5);
                if (imageCanvas is not null) imageCanvas.BrushCursorSize = toolService.Brush.Size;
                PopulateOptionsBar();
                imageCanvas?.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.OemCloseBrackets) // ]
            {
                if (hasShift)
                    toolService.Brush.Hardness = Math.Min(1, toolService.Brush.Hardness + 0.1);
                else
                    toolService.Brush.Size = Math.Min(5000, toolService.Brush.Size + 5);
                if (imageCanvas is not null) imageCanvas.BrushCursorSize = toolService.Brush.Size;
                PopulateOptionsBar();
                imageCanvas?.InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // Ctrl+key shortcuts (before tool single-key shortcuts)
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && !IsTextInputFocused())
        {
            bool hasShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool hasAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

            switch (e.Key)
            {
                case Key.Z when !hasShift:
                    OnUndo();
                    e.Handled = true;
                    return;
                case Key.Z when hasShift:
                case Key.Y:
                    OnRedo();
                    e.Handled = true;
                    return;
                case Key.N when !hasShift:
                    OnNewDocument();
                    e.Handled = true;
                    return;
                case Key.N when hasShift:
                    OnNewLayer();
                    e.Handled = true;
                    return;
                case Key.O:
                    OnOpenDocument();
                    e.Handled = true;
                    return;
                case Key.W:
                    OnCloseDocument();
                    e.Handled = true;
                    return;
                case Key.S when !hasShift:
                    OnSave();
                    e.Handled = true;
                    return;
                case Key.S when hasShift:
                    OnSaveAs();
                    e.Handled = true;
                    return;
                case Key.E when hasShift:
                    OnMergeVisible();
                    e.Handled = true;
                    return;
                case Key.J:
                    OnDuplicateLayer();
                    e.Handled = true;
                    return;
                case Key.A:
                    document?.SelectAll();
                    imageCanvas?.MarkDirty();
                    e.Handled = true;
                    return;
                case Key.D when !hasShift:
                    if (document is not null)
                    {
                        document.SelectionMask = null; // Deselect = no mask
                        imageCanvas?.MarkDirty();
                    }
                    e.Handled = true;
                    return;
                case Key.D when hasShift:
                    // Reselect — restore last selection if we had one
                    e.Handled = true;
                    return;
                case Key.I when hasShift && !hasAlt:
                    document?.InvertSelection();
                    imageCanvas?.MarkDirty();
                    e.Handled = true;
                    return;
                case Key.I when !hasShift && hasAlt:
                    OnImageSize();
                    e.Handled = true;
                    return;
                case Key.I when !hasShift && !hasAlt:
                    // Ctrl+I → Invert Colors
                    if (document is not null)
                        ApplyInstantFilter("Invert", () => SharpImage.Effects.ColorAdjust.Invert(document.GetActiveLayerImage()));
                    e.Handled = true;
                    return;
                case Key.X:
                    OnCut();
                    e.Handled = true;
                    return;
                case Key.C:
                    OnCopy();
                    e.Handled = true;
                    return;
                case Key.V when !hasShift:
                    OnPaste();
                    e.Handled = true;
                    return;
                case Key.V when hasShift:
                    OnPasteAsNew();
                    e.Handled = true;
                    return;
                case Key.L:
                    OpenLevels();
                    e.Handled = true;
                    return;
                case Key.M:
                    OpenCurves();
                    e.Handled = true;
                    return;
                case Key.U:
                    OpenHueSaturation();
                    e.Handled = true;
                    return;
                case Key.B:
                    OpenColorBalance();
                    e.Handled = true;
                    return;
                case Key.R:
                    // Toggle rulers
                    workspaceService.ShowRulers = !workspaceService.ShowRulers;
                    if (imageCanvas is not null) imageCanvas.ShowRulers = workspaceService.ShowRulers;
                    imageCanvas?.InvalidateVisual();
                    e.Handled = true;
                    return;
                case Key.OemPlus:
                case Key.Add:
                    OnZoomIn();
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    OnZoomOut();
                    e.Handled = true;
                    return;
                case Key.D0:
                    OnFitWindow();
                    e.Handled = true;
                    return;
                case Key.D1:
                    OnActualPixels();
                    e.Handled = true;
                    return;
            }
        }

        // Shift+F5 → Fill, Delete/Backspace → Delete selection
        if (!IsTextInputFocused())
        {
            // F12 → internal screenshot (Avalonia renderer, for debugging)
            if (e.Key == Key.F12)
            {
                TakeInternalScreenshot();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F5 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                OnFill();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F6 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                if (document?.HasSelection == true)
                {
                    document.ModifySelection("feather", 5);
                    imageCanvas?.MarkDirty();
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                OnDeleteSelection();
                e.Handled = true;
                return;
            }
        }

        // Tool shortcuts (single letter, no modifiers)
        if (e.KeyModifiers == KeyModifiers.None && !IsTextInputFocused())
        {
            switch (e.Key)
            {
                case Key.V: case Key.M: case Key.L: case Key.W:
                case Key.C: case Key.I: case Key.B: case Key.E:
                case Key.G: case Key.S: case Key.J: case Key.O:
                case Key.T: case Key.H: case Key.Z: case Key.P:
                case Key.U: case Key.R:
                    toolService.HandleShortcut(e.Key);
                    e.Handled = true;
                    return;

                case Key.D:
                    toolService.Brush.ResetColors();
                    foregroundColor = Color.FromUInt32(toolService.Brush.ForegroundColor);
                    backgroundColor = Color.FromUInt32(toolService.Brush.BackgroundColor);
                    colorSwatch?.SetColors(foregroundColor, backgroundColor);
                    UpdateColorFields();
                    e.Handled = true;
                    return;

                case Key.X:
                    toolService.Brush.SwapColors();
                    (foregroundColor, backgroundColor) = (backgroundColor, foregroundColor);
                    colorSwatch?.SetColors(foregroundColor, backgroundColor);
                    UpdateColorFields();
                    e.Handled = true;
                    return;
            }

            // Number keys 1-0 set tool opacity
            if (e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                int digit = e.Key - Key.D1 + 1;
                toolService.Brush.Opacity = digit / 10.0;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.D0)
            {
                toolService.Brush.Opacity = 1.0;
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        // Release Space: restore previous tool
        if (e.Key == Key.Space && isSpaceHeld)
        {
            isSpaceHeld = false;
            if (toolBeforeSpaceHand.HasValue)
            {
                toolService.ActiveToolType = toolBeforeSpaceHand.Value;
                toolBeforeSpaceHand = null;
            }
            e.Handled = true;
        }
    }

    private bool IsTextInputFocused()
    {
        return FocusManager?.GetFocusedElement() is TextBox;
    }

    private void TakeInternalScreenshot()
    {
        try
        {
            var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)Bounds.Width, (int)Bounds.Height));
            rtb.Render(this);
            rtb.Save(@"F:\Code\SharpImage\ss_avalonia.png");
            rtb.Dispose();
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  Menu Event Wiring
    // ═══════════════════════════════════════════════════

    private void WireMenuEvents()
    {
        // File
        MenuNew.Click += (_, _) => OnNewDocument();
        MenuOpen.Click += (_, _) => OnOpenDocument();
        MenuClose.Click += (_, _) => OnCloseDocument();
        MenuSave.Click += (_, _) => OnSave();
        MenuSaveAs.Click += (_, _) => OnSaveAs();
        MenuExport.Click += (_, _) => OnExport();
        MenuRevert.Click += (_, _) => OnRevert();
        MenuBatch.Click += (_, _) => OnBatchProcess();
        MenuPreferences.Click += (_, _) => OnPreferences();
        MenuExit.Click += (_, _) => Close();

        // Edit
        MenuUndo.Click += (_, _) => OnUndo();
        MenuRedo.Click += (_, _) => OnRedo();
        MenuCut.Click += (_, _) => OnCut();
        MenuCopy.Click += (_, _) => OnCopy();
        MenuPaste.Click += (_, _) => OnPaste();
        MenuPasteAsNew.Click += (_, _) => OnPasteAsNew();
        MenuFill.Click += (_, _) => OnFill();

        // View
        MenuZoomIn.Click += (_, _) => OnZoomIn();
        MenuZoomOut.Click += (_, _) => OnZoomOut();
        MenuFitWindow.Click += (_, _) => OnFitWindow();
        MenuActualPixels.Click += (_, _) => OnActualPixels();
        MenuRulers.Click += (_, _) =>
        {
            workspaceService.ShowRulers = !workspaceService.ShowRulers;
            if (imageCanvas is not null) imageCanvas.ShowRulers = workspaceService.ShowRulers;
            imageCanvas?.InvalidateVisual();
        };
        MenuPixelGrid.Click += (_, _) =>
        {
            workspaceService.ShowPixelGrid = !workspaceService.ShowPixelGrid;
            if (imageCanvas is not null) imageCanvas.ShowPixelGrid = workspaceService.ShowPixelGrid;
            imageCanvas?.InvalidateVisual();
        };
        MenuGrid.Click += (_, _) =>
        {
            workspaceService.ShowGrid = !workspaceService.ShowGrid;
            if (imageCanvas is not null) imageCanvas.ShowGrid = workspaceService.ShowGrid;
            imageCanvas?.InvalidateVisual();
        };

        // Window — panel visibility toggles
        MenuToggleNavigator.Click += (_, _) => workspaceService.TogglePanel("Navigator");
        MenuToggleColor.Click += (_, _) => workspaceService.TogglePanel("Color");
        MenuToggleLayers.Click += (_, _) => workspaceService.TogglePanel("Layers");
        MenuToggleHistory.Click += (_, _) => workspaceService.TogglePanel("History");
        MenuToggleProperties.Click += (_, _) => workspaceService.TogglePanel("Properties");
        MenuToggleChannels.Click += (_, _) => workspaceService.TogglePanel("Channels");
        MenuToggleBrushes.Click += (_, _) => workspaceService.TogglePanel("Brushes");
        MenuToggleAdjustments.Click += (_, _) => workspaceService.TogglePanel("Adjustments");
        MenuResetWorkspace.Click += (_, _) => OnResetWorkspace();

        // Image
        MenuImageSize.Click += (_, _) => OnImageSize();
        MenuCanvasSize.Click += (_, _) => OnCanvasSize();
        MenuFlatten.Click += (_, _) => OnFlattenImage();
        MenuFlipCanvasH.Click += (_, _) => OnFlipCanvas(horizontal: true);
        MenuFlipCanvasV.Click += (_, _) => OnFlipCanvas(horizontal: false);
        MenuRotateCanvasCW.Click += (_, _) => OnRotateCanvas(90);
        MenuRotateCanvasCCW.Click += (_, _) => OnRotateCanvas(270);
        MenuRotateCanvas180.Click += (_, _) => OnRotateCanvas(180);

        // Edit menu items
        MenuCut.Click += (_, _) => OnCut();
        MenuCopy.Click += (_, _) => OnCopy();
        MenuPaste.Click += (_, _) => OnPaste();
        MenuPasteAsNew.Click += (_, _) => OnPasteAsNew();
        MenuFill.Click += (_, _) => OnFill();
        MenuDelete.Click += (_, _) => OnDeleteSelection();

        // Layer
        MenuNewLayer.Click += (_, _) => OnNewLayer();
        MenuDuplicateLayer.Click += (_, _) => OnDuplicateLayer();
        MenuDeleteLayer.Click += (_, _) => OnDeleteLayer();
        MenuFlattenLayer.Click += (_, _) => OnFlattenImage();
        MenuMergeDown.Click += (_, _) => OnMergeDown();
        MenuMergeVisible.Click += (_, _) => OnMergeVisible();

        // Select
        MenuSelectAll.Click += (_, _) => { document?.SelectAll(); imageCanvas?.MarkDirty(); };
        MenuDeselect.Click += (_, _) => { if (document is not null) { document.SelectionMask = null; imageCanvas?.MarkDirty(); } };
        MenuInvertSelection.Click += (_, _) => { document?.InvertSelection(); imageCanvas?.MarkDirty(); };
        MenuSelDeselect.Click += (_, _) => { if (document is not null) { document.SelectionMask = null; imageCanvas?.MarkDirty(); } };
        MenuSelInverse.Click += (_, _) => { document?.InvertSelection(); imageCanvas?.MarkDirty(); };

        // Filters — Blur
        MenuGaussianBlur.Click += (_, _) => ShowFilterDialog("Gaussian Blur",
            [new() { Label = "Sigma", Min = 0.1, Max = 100, Default = 2.0, Step = 0.1 }],
            v => SharpImage.Effects.ConvolutionFilters.GaussianBlur(GetFilterSource(), v[0]));
        MenuBoxBlur.Click += (_, _) => ShowFilterDialog("Box Blur",
            [new() { Label = "Radius", Min = 1, Max = 100, Default = 3, IsInteger = true }],
            v => SharpImage.Effects.ConvolutionFilters.BoxBlur(GetFilterSource(), (int)v[0]));
        MenuMotionBlur.Click += (_, _) => ShowFilterDialog("Motion Blur",
            [new() { Label = "Radius", Min = 1, Max = 100, Default = 10, IsInteger = true },
             new() { Label = "Sigma", Min = 0.1, Max = 50, Default = 5.0, Step = 0.1 },
             new() { Label = "Angle", Min = 0, Max = 360, Default = 0, Step = 1 }],
            v => SharpImage.Effects.BlurNoiseOps.MotionBlur(GetFilterSource(), (int)v[0], v[1], v[2]));
        MenuRadialBlur.Click += (_, _) => ShowFilterDialog("Radial Blur",
            [new() { Label = "Angle", Min = 0.1, Max = 90, Default = 5.0, Step = 0.1 }],
            v => SharpImage.Effects.BlurNoiseOps.RadialBlur(GetFilterSource(), v[0]));
        MenuSelectiveBlur.Click += (_, _) => ShowFilterDialog("Selective Blur",
            [new() { Label = "Radius", Min = 1, Max = 50, Default = 3, IsInteger = true },
             new() { Label = "Sigma", Min = 0.1, Max = 20, Default = 2.0, Step = 0.1 },
             new() { Label = "Threshold", Min = 0, Max = 100, Default = 10, Step = 0.5 }],
            v => SharpImage.Effects.BlurNoiseOps.SelectiveBlur(GetFilterSource(), (int)v[0], v[1], v[2]));
        MenuLensBlur.Click += (_, _) => ShowFilterDialog("Lens Blur",
            [new() { Label = "Radius", Min = 1, Max = 30, Default = 5, IsInteger = true }],
            v => SharpImage.Effects.CreativeFilters.LensBlur(GetFilterSource(), (int)v[0]));
        MenuBilateralBlur.Click += (_, _) => ShowFilterDialog("Bilateral Blur",
            [new() { Label = "Radius", Min = 1, Max = 20, Default = 3, IsInteger = true },
             new() { Label = "Spatial Sigma", Min = 0.1, Max = 20, Default = 1.5, Step = 0.1 },
             new() { Label = "Range Sigma", Min = 1, Max = 200, Default = 50, Step = 1 }],
            v => SharpImage.Effects.BlurNoiseOps.BilateralBlur(GetFilterSource(), (int)v[0], v[1], v[2]));
        MenuTiltShift.Click += (_, _) => ShowFilterDialog("Tilt-Shift",
            [new() { Label = "Focus Y", Min = 0, Max = 1, Default = 0.5, Step = 0.01 },
             new() { Label = "Band Height", Min = 0.05, Max = 0.8, Default = 0.2, Step = 0.01 },
             new() { Label = "Blur Radius", Min = 1, Max = 30, Default = 8, IsInteger = true }],
            v => SharpImage.Effects.CreativeFilters.TiltShift(GetFilterSource(), v[0], v[1], (int)v[2]));

        // Filters — Sharpen
        MenuSharpenBasic.Click += (_, _) => ApplyInstantFilter("Sharpen",
            () => SharpImage.Effects.ConvolutionFilters.Sharpen(GetFilterSource()));
        MenuSharpenMore.Click += (_, _) => ApplyInstantFilter("Sharpen More",
            () => SharpImage.Effects.ConvolutionFilters.Sharpen(GetFilterSource(), 1.0, 2.0));
        MenuUnsharpMask.Click += (_, _) => ShowFilterDialog("Unsharp Mask",
            [new() { Label = "Sigma", Min = 0.1, Max = 50, Default = 1.0, Step = 0.1 },
             new() { Label = "Amount", Min = 0.1, Max = 5, Default = 1.0, Step = 0.1 },
             new() { Label = "Threshold", Min = 0, Max = 50, Default = 0, Step = 0.5 }],
            v => SharpImage.Effects.ConvolutionFilters.UnsharpMask(GetFilterSource(), v[0], v[1], v[2]));
        MenuSmartSharpen.Click += (_, _) => ShowFilterDialog("Smart Sharpen",
            [new() { Label = "Sigma", Min = 0.1, Max = 10, Default = 1.0, Step = 0.1 },
             new() { Label = "Amount", Min = 0.1, Max = 5, Default = 2.0, Step = 0.1 }],
            v => SharpImage.Effects.ConvolutionFilters.AdaptiveSharpen(GetFilterSource(), v[0], v[1]));

        // Filters — Noise
        MenuAddNoise.Click += (_, _) => ShowFilterDialog("Add Noise",
            [new() { Label = "Amount", Min = 0.1, Max = 100, Default = 10, Step = 0.5 }],
            v => SharpImage.Effects.BlurNoiseOps.AddNoise(GetFilterSource(), SharpImage.Effects.NoiseType.Gaussian, v[0]),
            [new() { Label = "Type", Options = ["Gaussian", "Uniform", "Impulse", "Poisson"], DefaultIndex = 0 }]);
        MenuDespeckle.Click += (_, _) => ApplyInstantFilter("Despeckle",
            () => SharpImage.Effects.BlurNoiseOps.Despeckle(GetFilterSource()));
        MenuReduceNoise.Click += (_, _) => ShowFilterDialog("Reduce Noise",
            [new() { Label = "Threshold", Min = 0.1, Max = 100, Default = 10, Step = 0.5 },
             new() { Label = "Softness", Min = 0, Max = 1, Default = 0, Step = 0.01 }],
            v => SharpImage.Effects.BlurNoiseOps.WaveletDenoise(GetFilterSource(), v[0], v[1]));
        MenuMedian.Click += (_, _) => ShowFilterDialog("Median Filter",
            [new() { Label = "Radius", Min = 1, Max = 20, Default = 1, IsInteger = true }],
            v => SharpImage.Effects.BlurNoiseOps.MedianFilter(GetFilterSource(), (int)v[0]));

        // Filters — Color
        MenuBrightnessContrast.Click += (_, _) => OpenBrightnessContrast();
        MenuLevels.Click += (_, _) => OpenLevels();
        MenuCurves.Click += (_, _) => OpenCurves();
        MenuHueSaturation.Click += (_, _) => OpenHueSaturation();
        MenuColorBalance.Click += (_, _) => OpenColorBalance();
        MenuVibrance.Click += (_, _) => OpenVibrance();
        MenuExposure.Click += (_, _) => OpenExposure();
        MenuDesaturate.Click += (_, _) => ApplyInstantFilter("Desaturate",
            () => SharpImage.Effects.ColorAdjust.Grayscale(GetFilterSource()));
        MenuInvert.Click += (_, _) => ApplyInstantFilter("Invert",
            () => SharpImage.Effects.ColorAdjust.Invert(GetFilterSource()));
        MenuColorPosterize.Click += (_, _) => OpenPosterize();
        MenuThreshold.Click += (_, _) => OpenThreshold();
        MenuAutoLevels.Click += (_, _) => ApplyInstantFilter("Auto Levels",
            () => SharpImage.Enhance.EnhanceOps.AutoLevel(GetFilterSource()));
        MenuAutoContrast.Click += (_, _) => ApplyInstantFilter("Auto Contrast",
            () => SharpImage.Enhance.EnhanceOps.SigmoidalContrast(GetFilterSource(), 3.0));
        MenuAutoColor.Click += (_, _) => ApplyInstantFilter("Auto Color",
            () => SharpImage.Enhance.EnhanceOps.WhiteBalance(GetFilterSource()));
        MenuEqualize.Click += (_, _) => ApplyInstantFilter("Equalize",
            () => SharpImage.Enhance.EnhanceOps.Equalize(GetFilterSource()));

        // Filters — Distort
        MenuSpherize.Click += (_, _) => ShowFilterDialog("Spherize",
            [new() { Label = "Amount", Min = -1, Max = 1, Default = 0.5, Step = 0.01 }],
            v => SharpImage.Effects.DisplacementOps.Spherize(GetFilterSource(), v[0]));
        MenuSwirl.Click += (_, _) => ShowFilterDialog("Swirl",
            [new() { Label = "Degrees", Min = -360, Max = 360, Default = 60, Step = 1 }],
            v => SharpImage.Effects.ArtisticOps.Swirl(GetFilterSource(), v[0]));
        MenuWave.Click += (_, _) => ShowFilterDialog("Wave",
            [new() { Label = "Amplitude", Min = 1, Max = 100, Default = 10, Step = 0.5 },
             new() { Label = "Wavelength", Min = 5, Max = 500, Default = 50, Step = 1 }],
            v => SharpImage.Effects.ArtisticOps.Wave(GetFilterSource(), v[0], v[1]));
        MenuRipple.Click += (_, _) => ShowFilterDialog("Ripple (Implode)",
            [new() { Label = "Amount", Min = -1, Max = 1, Default = 0.5, Step = 0.01 }],
            v => SharpImage.Effects.ArtisticOps.Implode(GetFilterSource(), v[0]));
        MenuBarrelDistortion.Click += (_, _) => ShowFilterDialog("Barrel Distortion",
            [new() { Label = "A", Min = -1, Max = 1, Default = 0, Step = 0.01 },
             new() { Label = "B", Min = -1, Max = 1, Default = 0, Step = 0.01 },
             new() { Label = "C", Min = -1, Max = 1, Default = 0, Step = 0.01 },
             new() { Label = "D", Min = 0, Max = 2, Default = 1, Step = 0.01 }],
            v => SharpImage.Transform.Distort.Barrel(GetFilterSource(), v[0], v[1], v[2], v[3]));
        MenuPolarCoordinates.Click += (_, _) => ApplyInstantFilter("Polar Coordinates",
            () => SharpImage.Transform.Distort.CartesianToPolar(GetFilterSource()));

        // Filters — Stylize
        MenuEmboss.Click += (_, _) => ApplyInstantFilter("Emboss",
            () => SharpImage.Effects.ConvolutionFilters.Emboss(GetFilterSource()));
        MenuFindEdges.Click += (_, _) => ApplyInstantFilter("Find Edges",
            () => SharpImage.Effects.ConvolutionFilters.EdgeDetect(GetFilterSource()));
        MenuOilPaint.Click += (_, _) => ShowFilterDialog("Oil Paint",
            [new() { Label = "Radius", Min = 1, Max = 10, Default = 3, IsInteger = true }],
            v => SharpImage.Effects.ArtisticOps.OilPaint(GetFilterSource(), (int)v[0]));
        MenuSolarize.Click += (_, _) => ApplyInstantFilter("Solarize",
            () => SharpImage.Enhance.EnhanceOps.Solarize(GetFilterSource()));

        // Filters — Artistic
        MenuCharcoal.Click += (_, _) => ShowFilterDialog("Charcoal",
            [new() { Label = "Radius", Min = 0.1, Max = 5, Default = 1.0, Step = 0.1 },
             new() { Label = "Sigma", Min = 0.1, Max = 3, Default = 0.5, Step = 0.1 }],
            v => SharpImage.Effects.ArtisticOps.Charcoal(GetFilterSource(), v[0], v[1]));
        MenuSketch.Click += (_, _) => ShowFilterDialog("Sketch",
            [new() { Label = "Sigma", Min = 0.1, Max = 5, Default = 0.5, Step = 0.1 },
             new() { Label = "Angle", Min = 0, Max = 180, Default = 45, Step = 1 }],
            v => SharpImage.Effects.ArtisticOps.Sketch(GetFilterSource(), v[0], v[1]));
        MenuPosterize.Click += (_, _) => ShowFilterDialog("Posterize",
            [new() { Label = "Levels", Min = 2, Max = 32, Default = 4, IsInteger = true }],
            v => SharpImage.Effects.ColorAdjust.Posterize(GetFilterSource(), (int)v[0]));
        MenuHalftone.Click += (_, _) => ShowFilterDialog("Halftone",
            [new() { Label = "Dot Size", Min = 2, Max = 20, Default = 6, IsInteger = true }],
            v => SharpImage.Effects.CreativeFilters.Halftone(GetFilterSource(), (int)v[0]));
        MenuPixelate.Click += (_, _) => ShowFilterDialog("Pixelate",
            [new() { Label = "Block Size", Min = 2, Max = 64, Default = 8, IsInteger = true }],
            v => SharpImage.Effects.CreativeFilters.Pixelate(GetFilterSource(), (int)v[0]));
        MenuCrystallize.Click += (_, _) => ShowFilterDialog("Crystallize",
            [new() { Label = "Cell Size", Min = 4, Max = 64, Default = 16, IsInteger = true }],
            v => SharpImage.Effects.CreativeFilters.Crystallize(GetFilterSource(), (int)v[0]));
        MenuPointillize.Click += (_, _) => ShowFilterDialog("Pointillize",
            [new() { Label = "Dot Radius", Min = 1, Max = 20, Default = 4, IsInteger = true }],
            v => SharpImage.Effects.CreativeFilters.Pointillize(GetFilterSource(), (int)v[0]));
        MenuVignette.Click += (_, _) => ShowFilterDialog("Vignette",
            [new() { Label = "Sigma", Min = 1, Max = 100, Default = 10, Step = 0.5 }],
            v => SharpImage.Effects.ArtisticOps.Vignette(GetFilterSource(), v[0]));

        // Filters — Generate (fills active layer or entire document)
        MenuClouds.Click += (_, _) => ApplyGenerateFilter("Clouds",
            () => SharpImage.Generators.NoiseGenerator.Turbulence(document!.Width, document.Height, colorize: true));
        MenuPlasma.Click += (_, _) => ApplyGenerateFilter("Plasma",
            () => SharpImage.Generators.PlasmaGenerator.Generate(document!.Width, document.Height));
        MenuCheckerboard.Click += (_, _) => ApplyGenerateFilter("Checkerboard",
            () => SharpImage.Generators.PatternGenerator.Generate(document!.Width, document.Height, SharpImage.Generators.PatternName.Checkerboard));

        // Filters — remaining color
        MenuGradientMap.Click += (_, _) => ApplyInstantFilter("Gradient Map",
            () =>
            {
                var src = GetFilterSource();
                (double, ushort, ushort, ushort)[] stops = [(0, 0, 0, 0), (1, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue)];
                return SharpImage.Effects.ColorGradingOps.GradientMap(src, stops);
            });
        MenuChannelMixer.Click += (_, _) => ShowFilterDialog("Channel Mixer",
            [new() { Label = "R→R", Min = 0, Max = 2, Default = 1, Step = 0.01 },
             new() { Label = "R→G", Min = -1, Max = 1, Default = 0, Step = 0.01 },
             new() { Label = "R→B", Min = -1, Max = 1, Default = 0, Step = 0.01 },
             new() { Label = "G→R", Min = -1, Max = 1, Default = 0, Step = 0.01 },
             new() { Label = "G→G", Min = 0, Max = 2, Default = 1, Step = 0.01 },
             new() { Label = "G→B", Min = -1, Max = 1, Default = 0, Step = 0.01 },
             new() { Label = "B→R", Min = -1, Max = 1, Default = 0, Step = 0.01 },
             new() { Label = "B→G", Min = -1, Max = 1, Default = 0, Step = 0.01 },
             new() { Label = "B→B", Min = 0, Max = 2, Default = 1, Step = 0.01 }],
            v => SharpImage.Effects.ColorGradingOps.ChannelMixer(GetFilterSource(),
                v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]));
        MenuShadowHighlight.Click += (_, _) => ShowFilterDialog("Shadow/Highlight",
            [new() { Label = "Dehaze Strength", Min = 0, Max = 1, Default = 0.5, Step = 0.01 }],
            v => SharpImage.Effects.ColorAdjust.Dehaze(GetFilterSource(), v[0]));

        // Remaining filters
        MenuSurfaceBlur.Click += (_, _) => ShowFilterDialog("Surface Blur",
            [new() { Label = "Radius", Min = 1, Max = 20, Default = 3, IsInteger = true },
             new() { Label = "Sigma", Min = 0.1, Max = 20, Default = 2, Step = 0.1 },
             new() { Label = "Threshold", Min = 0, Max = 100, Default = 15, Step = 0.5 }],
            v => SharpImage.Effects.BlurNoiseOps.SelectiveBlur(GetFilterSource(), (int)v[0], v[1], v[2]));
        MenuHighPass.Click += (_, _) => ShowFilterDialog("High Pass",
            [new() { Label = "Radius", Min = 0.1, Max = 50, Default = 3, Step = 0.1 }],
            v => SharpImage.Effects.ConvolutionFilters.EdgeDetect(GetFilterSource(), v[0]));
        MenuNoiseGenerator.Click += (_, _) => ShowFilterDialog("Noise Generator",
            [new() { Label = "Frequency", Min = 1, Max = 32, Default = 4, Step = 0.5 }],
            v => SharpImage.Generators.NoiseGenerator.Perlin(document!.Width, document.Height, v[0]));
        MenuGradientGen.Click += (_, _) => ApplyGenerateFilter("Gradient",
            () => SharpImage.Generators.GradientGenerator.Linear(
                document!.Width, document.Height, 0, 0, 0, 255, 255, 255));
        MenuPattern.Click += (_, _) => ApplyGenerateFilter("Pattern",
            () => SharpImage.Generators.PatternGenerator.Generate(document!.Width, document.Height, SharpImage.Generators.PatternName.Checkerboard));

        // Edit menu — Crop to selection, Stroke
        MenuCropToSelection.Click += (_, _) => OnCropToSelection();
        MenuStroke.Click += (_, _) => { }; // Requires selection stroke dialog — future work

        // Select menu duplicates
        MenuSelAll.Click += (_, _) => { document?.SelectAll(); imageCanvas?.MarkDirty(); };
        MenuSelReselect.Click += (_, _) => { }; // Reselect requires storing last selection — future
        MenuReselect.Click += (_, _) => { };

        // Transform menu
        MenuTransformFlipH.Click += (_, _) => OnFlipActiveLayer(horizontal: true);
        MenuTransformFlipV.Click += (_, _) => OnFlipActiveLayer(horizontal: false);
        MenuTransformRotateCW.Click += (_, _) => OnRotateActiveLayer(90);
        MenuTransformRotateCCW.Click += (_, _) => OnRotateActiveLayer(270);
        MenuTransformRotate180.Click += (_, _) => OnRotateActiveLayer(180);
        MenuFreeTransform.Click += (_, _) => { }; // Complex transform tool — future

        // Layer grouping (simple stubs for now)
        MenuGroupLayers.Click += (_, _) => { };
        MenuUngroupLayers.Click += (_, _) => { };
        MenuLayerProperties.Click += (_, _) => { };

        // Selection modifications
        MenuSelBorder.Click += (_, _) => { if (document?.HasSelection == true) document.ModifySelection("border", 3); imageCanvas?.MarkDirty(); };
        MenuSelSmooth.Click += (_, _) => { if (document?.HasSelection == true) document.ModifySelection("smooth", 2); imageCanvas?.MarkDirty(); };
        MenuSelExpand.Click += (_, _) => { if (document?.HasSelection == true) document.ModifySelection("expand", 3); imageCanvas?.MarkDirty(); };
        MenuSelContract.Click += (_, _) => { if (document?.HasSelection == true) document.ModifySelection("contract", 3); imageCanvas?.MarkDirty(); };
        MenuSelFeather.Click += (_, _) => { if (document?.HasSelection == true) document.ModifySelection("feather", 5); imageCanvas?.MarkDirty(); };
        MenuSelGrow.Click += (_, _) => { if (document?.HasSelection == true) document.ModifySelection("grow", 5); imageCanvas?.MarkDirty(); };
        MenuSelSimilar.Click += (_, _) => { if (document?.HasSelection == true) document.ModifySelection("similar", 30); imageCanvas?.MarkDirty(); };
        MenuColorRange.Click += (_, _) => { }; // Complex — needs dedicated dialog

        // View toggles
        MenuShowSelectionEdges.Click += (_, _) => { workspaceService.ShowSelectionEdges = !workspaceService.ShowSelectionEdges; imageCanvas?.MarkDirty(); };
        MenuShowLayerEdges.Click += (_, _) => { }; // Future: show layer boundary outline
        MenuShowGuides.Click += (_, _) => { workspaceService.ShowGuides = !workspaceService.ShowGuides; imageCanvas?.MarkDirty(); };
        MenuShowGrid.Click += (_, _) =>
        {
            workspaceService.ShowGrid = !workspaceService.ShowGrid;
            if (imageCanvas is not null) imageCanvas.ShowGrid = workspaceService.ShowGrid;
            imageCanvas?.MarkDirty();
        };
        MenuShowPixelGrid.Click += (_, _) =>
        {
            workspaceService.ShowPixelGrid = !workspaceService.ShowPixelGrid;
            if (imageCanvas is not null) imageCanvas.ShowPixelGrid = workspaceService.ShowPixelGrid;
            imageCanvas?.MarkDirty();
        };
        MenuGuides.Click += (_, _) => { workspaceService.ShowGuides = !workspaceService.ShowGuides; imageCanvas?.MarkDirty(); };

        // Image operations
        MenuTrim.Click += (_, _) => { }; // Future: auto-trim transparent/uniform edges
        MenuAutoCrop.Click += (_, _) => { }; // Future: detect and remove uniform borders
        MenuModeRGB.Click += (_, _) => { }; // Document is always RGB currently
        MenuModeGrayscale.Click += (_, _) =>
        {
            if (document is null) return;
            for (int i = 0; i < document.Layers.Count; i++)
            {
                var layer = document.Layers[i];
                if (layer.Content is not null)
                    layer.Content = SharpImage.Effects.ColorAdjust.Grayscale(layer.Content);
            }
            document.IsDirty = true;
            undoService.Clear();
            imageCanvas?.MarkDirty();
        };
        MenuRotateCanvasArbitrary.Click += (_, _) => { }; // Future: arbitrary angle dialog

        // Complex filters without full UI yet
        MenuLiquify.Click += (_, _) => { }; // Needs dedicated interactive UI
        MenuDisplacementMap.Click += (_, _) => { }; // Needs second image input
        MenuColorLUT.Click += (_, _) => { }; // Needs LUT file picker
        MenuHDRToning.Click += (_, _) => { }; // HDR specific
        MenuCustomConvolution.Click += (_, _) => { }; // Needs kernel input grid
        MenuMinMax.Click += (_, _) => ShowFilterDialog("Minimum/Maximum",
            [new() { Label = "Radius", Min = 1, Max = 20, Default = 1, IsInteger = true }],
            v => SharpImage.Effects.BlurNoiseOps.MedianFilter(GetFilterSource(), (int)v[0]));
        MenuFFT.Click += (_, _) => { }; // Fourier operations — future
        MenuInverseFFT.Click += (_, _) => { };

        // Help
        MenuKeyboardShortcuts.Click += (_, _) => OnKeyboardShortcuts();
        MenuAbout.Click += (_, _) => OnAbout();
    }

    // ═══════════════════════════════════════════════════
    //  Document Operations (placeholders wired to menus)
    // ═══════════════════════════════════════════════════

    private async void OnNewDocument()
    {
        var dialog = new Dialogs.NewDocumentDialog();
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;

        undoService.Clear();
        document = new EditorDocument(dialog.DocumentWidth, dialog.DocumentHeight);

        // Fill background based on choice
        if (dialog.BackgroundFill != "Transparent")
        {
            var frame = document.GetActiveLayerImage();
            ushort fillR = ushort.MaxValue, fillG = ushort.MaxValue, fillB = ushort.MaxValue;
            if (dialog.BackgroundFill == "Black") { fillR = 0; fillG = 0; fillB = 0; }
            for (int y = 0; y < (int)frame.Rows; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < (int)frame.Columns; x++)
                {
                    int channels = frame.NumberOfChannels;
                    int off = x * channels;
                    row[off] = fillR; row[off + 1] = fillG; row[off + 2] = fillB;
                    if (frame.HasAlpha && channels > 3) row[off + 3] = ushort.MaxValue;
                }
            }
        }
        imageCanvas?.SetDocument(document);
        EmptyCanvasMessage.IsVisible = false;
        SyncActiveTool();
        RefreshLayersPanel();
        UpdateColorFields();
        UpdateStatusBar();
        UpdateTitle();
    }

    private async void OnOpenDocument()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tga", "*.webp", "*.gif", "*.tiff", "*.tif", "*.qoi", "*.hdr", "*.psd", "*.ico"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });

        if (files is { Count: > 0 })
            OpenFile(files[0].Path.LocalPath);
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // e.Data is obsolete but DataTransfer API differs
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files is null) return;

        foreach (var item in files)
        {
            if (item is not Avalonia.Platform.Storage.IStorageFile storageFile) continue;
            string path = storageFile.Path.LocalPath;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            string[] supported = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tga", ".tiff", ".tif", ".qoi", ".hdr", ".gif", ".psd", ".ico"];
            if (supported.Contains(ext))
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && document is not null)
                {
                    try
                    {
                        var frame = FormatRegistry.Read(path);
                        var layer = new SharpImage.Layers.Layer(System.IO.Path.GetFileName(path))
                        {
                            Content = frame
                        };
                        document.Layers.AddLayer(layer);
                        document.ActiveLayerIndex = document.Layers.Count - 1;
                        document.IsDirty = true;
                        undoService.Push(new AddLayerCommand("Drop as Layer",
                            document.Layers.Count - 1, layer));
                        imageCanvas?.MarkDirty();
                        RefreshLayersPanel();
                    }
                    catch { }
                }
                else
                {
                    OpenFile(path);
                }
                break;
            }
        }
        await Task.CompletedTask; // suppress async warning
    }

    private void OpenFile(string path)
    {
        try
        {
            var image = FormatRegistry.Read(path);
            undoService.Clear();
            document = new EditorDocument(image, path);
            imageCanvas?.SetDocument(document);
            EmptyCanvasMessage.IsVisible = false;
            SyncActiveTool();
            RefreshLayersPanel();
            RefreshNavigatorPreview();
            UpdateColorFields();
            UpdateStatusBar();
            UpdateTitle();

            // Track in recent files
            var recent = settingsService.Settings.RecentFiles;
            recent.Remove(path);
            recent.Insert(0, path);
            while (recent.Count > settingsService.Settings.RecentFilesCount)
                recent.RemoveAt(recent.Count - 1);
        }
        catch (Exception ex)
        {
            StatusFileName.Text = $"Error: {ex.Message}";
        }
    }

    private void OnCloseDocument()
    {
        document = null;
        undoService.Clear();
        imageCanvas?.SetDocument(null);
        SyncActiveTool();
        RefreshLayersPanel();
        RefreshNavigatorPreview();
        EmptyCanvasMessage.IsVisible = true;
        UpdateStatusBar();
        UpdateTitle();
    }

    private void OnSave()
    {
        if (document is null) return;
        if (document.FilePath is not null)
        {
            SaveDocument(document.FilePath);
        }
        else
        {
            OnSaveAs();
        }
    }

    private async void OnSaveAs()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save As",
            DefaultExtension = "png",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("PNG") { Patterns = ["*.png"] },
                new Avalonia.Platform.Storage.FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] },
                new Avalonia.Platform.Storage.FilePickerFileType("BMP") { Patterns = ["*.bmp"] },
                new Avalonia.Platform.Storage.FilePickerFileType("WebP") { Patterns = ["*.webp"] },
                new Avalonia.Platform.Storage.FilePickerFileType("TIFF") { Patterns = ["*.tiff", "*.tif"] },
                new Avalonia.Platform.Storage.FilePickerFileType("TGA") { Patterns = ["*.tga"] },
                new Avalonia.Platform.Storage.FilePickerFileType("QOI") { Patterns = ["*.qoi"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });

        if (file is not null && document is not null)
        {
            document.FilePath = file.Path.LocalPath;
            SaveDocument(document.FilePath);
        }
    }

    private void SaveDocument(string path)
    {
        if (document is null) return;
        try
        {
            var flattened = document.Flatten();
            FormatRegistry.Write(flattened, path);
            document.IsDirty = false;
            UpdateStatusBar();
            UpdateTitle();
        }
        catch (Exception ex)
        {
            StatusFileName.Text = $"Save error: {ex.Message}";
        }
    }

    private async void OnExport()
    {
        if (document is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export As",
            DefaultExtension = "png",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("PNG") { Patterns = ["*.png"] },
                new Avalonia.Platform.Storage.FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] },
                new Avalonia.Platform.Storage.FilePickerFileType("WebP") { Patterns = ["*.webp"] },
                new Avalonia.Platform.Storage.FilePickerFileType("BMP") { Patterns = ["*.bmp"] },
                new Avalonia.Platform.Storage.FilePickerFileType("TIFF") { Patterns = ["*.tiff", "*.tif"] },
                new Avalonia.Platform.Storage.FilePickerFileType("TGA") { Patterns = ["*.tga"] },
                new Avalonia.Platform.Storage.FilePickerFileType("GIF") { Patterns = ["*.gif"] },
                new Avalonia.Platform.Storage.FilePickerFileType("QOI") { Patterns = ["*.qoi"] },
                new Avalonia.Platform.Storage.FilePickerFileType("HDR") { Patterns = ["*.hdr"] },
                new Avalonia.Platform.Storage.FilePickerFileType("ICO") { Patterns = ["*.ico"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });

        if (file is not null)
        {
            try
            {
                var flattened = document.Flatten();
                FormatRegistry.Write(flattened, file.Path.LocalPath);
                StatusFileName.Text = $"Exported to {System.IO.Path.GetFileName(file.Path.LocalPath)}";
            }
            catch (Exception ex)
            {
                StatusFileName.Text = $"Export error: {ex.Message}";
            }
        }
    }

    private void OnRevert()
    {
        if (document?.FilePath is not null)
            OpenFile(document.FilePath);
    }

    private async void OnBatchProcess()
    {
        var dialog = new Dialogs.BatchProcessDialog();
        dialog.StartProcessing += async (_, _) =>
        {
            var sourceDir = dialog.SourceFolder;
            var outputDir = dialog.OutputFolder;
            if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(outputDir)) return;
            if (!System.IO.Directory.Exists(sourceDir)) return;
            if (!System.IO.Directory.Exists(outputDir))
                System.IO.Directory.CreateDirectory(outputDir);

            var searchOption = dialog.IsRecursive
                ? System.IO.SearchOption.AllDirectories
                : System.IO.SearchOption.TopDirectoryOnly;

            string[] extensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tga", ".tiff", ".tif", ".qoi", ".hdr"];
            var files = System.IO.Directory.GetFiles(sourceDir, "*.*", searchOption)
                .Where(f => extensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                .ToArray();

            if (files.Length == 0) return;
            dialog.ShowProgress();

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string fileName = System.IO.Path.GetFileNameWithoutExtension(file);
                string ext = System.IO.Path.GetExtension(file);

                dialog.StatusText.Text = $"Processing: {System.IO.Path.GetFileName(file)}";
                dialog.FileCountText.Text = $"{i + 1} / {files.Length}";
                dialog.BatchProgressBar.Value = (double)i / files.Length * 100;

                try
                {
                    var frame = SharpImage.Formats.FormatRegistry.Read(file);

                    // Apply resize if enabled
                    if (dialog.ResizeEnabled && dialog.ResizeWidth > 0 && dialog.ResizeHeight > 0)
                    {
                        frame = SharpImage.Transform.Resize.Apply(frame, dialog.ResizeWidth, dialog.ResizeHeight,
                            SharpImage.Transform.InterpolationMethod.Lanczos3);
                    }

                    // Apply filter if enabled
                    if (dialog.FilterEnabled && !string.IsNullOrEmpty(dialog.FilterName))
                    {
                        frame = dialog.FilterName switch
                        {
                            "Sharpen" => SharpImage.Effects.ConvolutionFilters.Sharpen(frame),
                            "Auto Levels" => SharpImage.Enhance.EnhanceOps.AutoLevel(frame),
                            "Desaturate" => SharpImage.Effects.ColorAdjust.Grayscale(frame),
                            "Auto Contrast" => SharpImage.Enhance.EnhanceOps.SigmoidalContrast(frame, 3.0),
                            _ => frame,
                        };
                    }

                    // Determine output name
                    string outName = dialog.NamingMode switch
                    {
                        "Add Prefix" => dialog.NamingText + fileName,
                        "Add Suffix" => fileName + dialog.NamingText,
                        _ => fileName,
                    };

                    // Determine output format
                    string outExt = dialog.ConvertEnabled && !string.IsNullOrEmpty(dialog.TargetFormat)
                        ? "." + dialog.TargetFormat.ToLowerInvariant()
                        : ext;

                    string outputPath = System.IO.Path.Combine(outputDir, outName + outExt);
                    SharpImage.Formats.FormatRegistry.Write(frame, outputPath, dialog.Quality);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Batch error on {file}: {ex.Message}");
                }

                // Yield to UI thread
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            }

            dialog.StatusText.Text = "Complete!";
            dialog.BatchProgressBar.Value = 100;
            dialog.FileCountText.Text = $"{files.Length} / {files.Length}";
        };
        await dialog.ShowDialog(this);
    }

    private async void OnPreferences()
    {
        var dialog = new Dialogs.PreferencesDialog();
        dialog.UndoLevels = undoService.MaxUndoLevels;
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;

        undoService.MaxUndoLevels = dialog.UndoLevels;
        // Theme preference stored but requires app restart for full effect
    }

    private void OnUndo()
    {
        if (document is null || !undoService.CanUndo) return;
        undoService.Undo(document);
        document.IsDirty = true;
        imageCanvas?.MarkDirty();
        RefreshHistoryPanel();
        RefreshLayersPanel();
        UpdateStatusBar();
        UpdateTitle();
    }

    private void OnRedo()
    {
        if (document is null || !undoService.CanRedo) return;
        undoService.Redo(document);
        document.IsDirty = true;
        imageCanvas?.MarkDirty();
        RefreshHistoryPanel();
        RefreshLayersPanel();
        UpdateStatusBar();
        UpdateTitle();
    }

    // Clipboard buffer (stored as raw ImageFrame)
    private SharpImage.Image.ImageFrame? clipboardImage;

    private void OnCopy()
    {
        if (document is null) return;
        var flattened = document.Flatten();
        if (document.HasSelection && document.SelectionMask is not null)
        {
            // Copy only selected region to clipboard
            clipboardImage = ExtractSelectedRegion(flattened, document.SelectionMask, document.Width, document.Height);
        }
        else
        {
            clipboardImage = flattened.Clone();
        }
    }

    private void OnCut()
    {
        if (document is null) return;
        OnCopy();
        OnDeleteSelection();
    }

    private void OnPaste()
    {
        if (document is null || clipboardImage is null) return;
        var layer = new SharpImage.Layers.Layer("Pasted Layer")
        {
            Content = clipboardImage.Clone(),
        };
        int insertIndex = document.Layers.Count;
        document.Layers.AddLayer(layer);
        document.ActiveLayerIndex = insertIndex;
        document.IsDirty = true;
        undoService.Push(new AddLayerCommand("Paste", insertIndex, layer));
        imageCanvas?.MarkDirty();
        RefreshLayersPanel();
        UpdateStatusBar();
        UpdateTitle();
    }

    private void OnPasteAsNew()
    {
        if (clipboardImage is null) return;
        undoService.Clear();
        document = new EditorDocument(clipboardImage.Clone(), null);
        imageCanvas?.SetDocument(document);
        EmptyCanvasMessage.IsVisible = false;
        SyncActiveTool();
        RefreshLayersPanel();
        UpdateColorFields();
        UpdateStatusBar();
        UpdateTitle();
    }

    private void OnDeleteSelection()
    {
        if (document is null) return;
        BeginPixelOperation();
        var frame = document.GetActiveLayerImage();
        int channels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;

        if (document.HasSelection && document.SelectionMask is not null)
        {
            var mask = document.SelectionMask;
            for (int y = 0; y < (int)frame.Rows; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < (int)frame.Columns; x++)
                {
                    if (mask[y * document.Width + x] > 0)
                    {
                        int offset = x * channels;
                        if (hasAlpha && channels > 3)
                        {
                            row[offset + 3] = 0; // Set alpha to 0 (transparent)
                        }
                        else
                        {
                            row[offset] = ushort.MaxValue;
                            row[offset + 1] = ushort.MaxValue;
                            row[offset + 2] = ushort.MaxValue;
                        }
                    }
                }
            }
        }
        CommitPixelChange("Delete");
        imageCanvas?.MarkDirty();
    }

    private void OnFill()
    {
        if (document is null) return;
        BeginPixelOperation();
        var frame = document.GetActiveLayerImage();
        int channels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;
        ushort fR = (ushort)(foregroundColor.R * 257);
        ushort fG = (ushort)(foregroundColor.G * 257);
        ushort fB = (ushort)(foregroundColor.B * 257);
        ushort fA = (ushort)(foregroundColor.A * 257);

        for (int y = 0; y < (int)frame.Rows; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)frame.Columns; x++)
            {
                bool inSelection = document.SelectionMask is null ||
                    document.SelectionMask[y * document.Width + x] > 0;
                if (!inSelection) continue;

                int offset = x * channels;
                row[offset] = fR;
                row[offset + 1] = fG;
                row[offset + 2] = fB;
                if (hasAlpha && channels > 3)
                    row[offset + 3] = fA;
            }
        }
        CommitPixelChange("Fill");
        imageCanvas?.MarkDirty();
    }

    private static SharpImage.Image.ImageFrame ExtractSelectedRegion(
        SharpImage.Image.ImageFrame source, byte[] mask, int width, int height)
    {
        var result = source.Clone();
        int channels = result.NumberOfChannels;
        bool hasAlpha = result.HasAlpha;

        for (int y = 0; y < height; y++)
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                if (mask[y * width + x] == 0)
                {
                    int offset = x * channels;
                    row[offset] = 0;
                    row[offset + 1] = 0;
                    row[offset + 2] = 0;
                    if (hasAlpha && channels > 3)
                        row[offset + 3] = 0;
                }
            }
        }
        return result;
    }

    private void OnZoomIn() => imageCanvas?.ZoomIn();
    private void OnZoomOut() => imageCanvas?.ZoomOut();
    private void OnFitWindow() => imageCanvas?.FitToView();
    private void OnActualPixels() => imageCanvas?.ActualPixels();

    /// <summary>
    /// Takes a snapshot of the active layer's pixels BEFORE a destructive operation.
    /// Call this, perform the operation, then call CommitPixelChange with the returned snapshot.
    /// </summary>
    private SharpImage.Image.ImageFrame? BeginPixelOperation()
    {
        if (document is null) return null;
        pixelBeforeSnapshot = document.GetActiveLayerImage().Clone();
        return pixelBeforeSnapshot;
    }

    /// <summary>
    /// Completes a pixel operation by pushing an undo command with the before/after states.
    /// </summary>
    private void CommitPixelChange(string description, SharpImage.Image.ImageFrame beforeSnapshot)
    {
        if (document is null) return;
        var afterSnapshot = document.GetActiveLayerImage().Clone();
        undoService.Push(new PixelChangeCommand(description, document.ActiveLayerIndex, beforeSnapshot, afterSnapshot));
        document.IsDirty = true;
        imageCanvas?.MarkDirty();
        UpdateStatusBar();
        UpdateTitle();
    }

    /// <summary>
    /// Completes a pixel operation using the stored BeginPixelOperation snapshot.
    /// </summary>
    private void CommitPixelChange(string description)
    {
        if (pixelBeforeSnapshot is null) return;
        CommitPixelChange(description, pixelBeforeSnapshot);
        pixelBeforeSnapshot = null;
    }

    private void OnFlattenImage()
    {
        if (document is null || document.Layers.Count <= 1) return;
        var flattened = document.Flatten();

        // Remove all layers except keep one
        while (document.Layers.Count > 1)
            document.Layers.RemoveLayer(document.Layers.Count - 1);

        document.Layers[0].Content = flattened;
        document.Layers[0].Visible = true;
        document.Layers[0].Opacity = 1.0;
        document.ActiveLayerIndex = 0;
        document.IsDirty = true;
        undoService.Clear(); // Flatten is not easily undoable
        imageCanvas?.MarkDirty();
        RefreshLayersPanel();
        RefreshHistoryPanel();
        UpdateStatusBar();
        UpdateTitle();
    }

    private void OnMergeDown()
    {
        if (document is null || document.ActiveLayerIndex == 0) return;
        int topIdx = document.ActiveLayerIndex;
        int bottomIdx = topIdx - 1;
        var topLayer = document.Layers[topIdx];
        var bottomLayer = document.Layers[bottomIdx];

        // Composite top onto bottom
        MergeLayerOnto(topLayer, bottomLayer, document.Width, document.Height);

        document.Layers.RemoveLayer(topIdx);
        document.ActiveLayerIndex = bottomIdx;
        document.IsDirty = true;
        undoService.Clear(); // Merge is complex to undo, clear for now
        imageCanvas?.MarkDirty();
        RefreshLayersPanel();
        RefreshHistoryPanel();
        UpdateStatusBar();
        UpdateTitle();
    }

    private void OnMergeVisible()
    {
        if (document is null) return;
        // Find all visible layers
        var visibleIndices = new List<int>();
        for (int i = 0; i < document.Layers.Count; i++)
        {
            if (document.Layers[i].Visible)
                visibleIndices.Add(i);
        }
        if (visibleIndices.Count <= 1) return;

        // Flatten visible layers into the bottom-most visible layer
        int targetIdx = visibleIndices[0];
        var targetLayer = document.Layers[targetIdx];
        for (int i = 1; i < visibleIndices.Count; i++)
        {
            MergeLayerOnto(document.Layers[visibleIndices[i]], targetLayer, document.Width, document.Height);
        }

        // Remove merged layers (from top to bottom to preserve indices)
        for (int i = visibleIndices.Count - 1; i > 0; i--)
            document.Layers.RemoveLayer(visibleIndices[i]);

        document.ActiveLayerIndex = Math.Min(targetIdx, document.Layers.Count - 1);
        document.IsDirty = true;
        undoService.Clear();
        imageCanvas?.MarkDirty();
        RefreshLayersPanel();
        RefreshHistoryPanel();
        UpdateStatusBar();
        UpdateTitle();
    }

    private static void MergeLayerOnto(SharpImage.Layers.Layer source, SharpImage.Layers.Layer target, int docWidth, int docHeight)
    {
        var srcFrame = source.Content;
        var dstFrame = target.Content;
        if (srcFrame is null || dstFrame is null) return;

        double opacity = source.Opacity;
        int channels = dstFrame.NumberOfChannels;

        for (int y = 0; y < (int)dstFrame.Rows && y < (int)srcFrame.Rows; y++)
        {
            var srcRow = srcFrame.GetPixelRow(y);
            var dstRow = dstFrame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)dstFrame.Columns && x < (int)srcFrame.Columns; x++)
            {
                int offset = x * channels;
                for (int c = 0; c < Math.Min(channels, srcFrame.NumberOfChannels); c++)
                {
                    ushort src = srcRow[x * srcFrame.NumberOfChannels + c];
                    ushort dst = dstRow[offset + c];
                    dstRow[offset + c] = (ushort)(dst + (src - dst) * opacity);
                }
            }
        }
    }

    private async void OnImageSize()
    {
        if (document is null) return;
        var dialog = new Dialogs.ResizeDialog(document.Width, document.Height);
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;
        if (dialog.NewWidth == document.Width && dialog.NewHeight == document.Height) return;

        // Resize every layer
        for (int i = 0; i < document.Layers.Count; i++)
        {
            var layer = document.Layers[i];
            if (layer.Content is not null)
            {
                var resized = SharpImage.Transform.Resize.Apply(layer.Content, dialog.NewWidth, dialog.NewHeight, dialog.Method);
                layer.Content = resized;
            }
        }
        document.Resize(dialog.NewWidth, dialog.NewHeight);
        document.SelectionMask = null;
        document.IsDirty = true;
        undoService.Clear();
        imageCanvas?.SetDocument(document);
        RefreshLayersPanel();
        UpdateStatusBar();
        UpdateTitle();
    }

    private async void OnCanvasSize()
    {
        if (document is null) return;
        var dialog = new Dialogs.CanvasSizeDialog(document.Width, document.Height);
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;
        if (dialog.NewWidth == document.Width && dialog.NewHeight == document.Height) return;

        int oldW = document.Width, oldH = document.Height;
        int newW = dialog.NewWidth, newH = dialog.NewHeight;

        // Calculate offset based on anchor
        int offsetX = dialog.AnchorX switch { 0 => 0, 1 => (newW - oldW) / 2, _ => newW - oldW };
        int offsetY = dialog.AnchorY switch { 0 => 0, 1 => (newH - oldH) / 2, _ => newH - oldH };

        for (int i = 0; i < document.Layers.Count; i++)
        {
            var layer = document.Layers[i];
            if (layer.Content is null) continue;
            var oldFrame = layer.Content;
            var newFrame = new SharpImage.Image.ImageFrame();
            newFrame.Initialize(newW, newH, hasAlpha: oldFrame.HasAlpha);
            int channels = newFrame.NumberOfChannels;

            // Copy old pixels into new frame at offset
            for (int y = 0; y < oldH; y++)
            {
                int dstY = y + offsetY;
                if (dstY < 0 || dstY >= newH) continue;
                var srcRow = oldFrame.GetPixelRow(y);
                var dstRow = newFrame.GetPixelRowForWrite(dstY);
                for (int x = 0; x < oldW; x++)
                {
                    int dstX = x + offsetX;
                    if (dstX < 0 || dstX >= newW) continue;
                    int srcOff = x * channels;
                    int dstOff = dstX * channels;
                    for (int c = 0; c < channels; c++)
                        dstRow[dstOff + c] = srcRow[srcOff + c];
                }
            }
            layer.Content = newFrame;
        }
        document.Resize(newW, newH);
        document.SelectionMask = null;
        document.IsDirty = true;
        undoService.Clear();
        imageCanvas?.SetDocument(document);
        RefreshLayersPanel();
        UpdateStatusBar();
        UpdateTitle();
    }

    private void OnFlipCanvas(bool horizontal)
    {
        if (document is null) return;
        for (int i = 0; i < document.Layers.Count; i++)
        {
            var layer = document.Layers[i];
            if (layer.Content is null) continue;
            layer.Content = horizontal
                ? SharpImage.Transform.Geometry.Flop(layer.Content)
                : SharpImage.Transform.Geometry.Flip(layer.Content);
        }
        document.IsDirty = true;
        undoService.Clear();
        imageCanvas?.MarkDirty();
        UpdateStatusBar();
        UpdateTitle();
    }

    private void OnRotateCanvas(int degrees)
    {
        if (document is null) return;

        BeginPixelOperation();

        var angle = degrees switch
        {
            90 => SharpImage.Transform.RotationAngle.Rotate90,
            180 => SharpImage.Transform.RotationAngle.Rotate180,
            270 => SharpImage.Transform.RotationAngle.Rotate270,
            _ => SharpImage.Transform.RotationAngle.Rotate90,
        };

        try
        {
            for (int i = 0; i < document.Layers.Count; i++)
            {
                var layer = document.Layers[i];
                if (layer.Content is null) continue;
                layer.Content = SharpImage.Transform.Geometry.Rotate(layer.Content, angle);
            }

            // 90/270 swaps dimensions
            if (degrees == 90 || degrees == 270)
                document.Resize(document.Height, document.Width);

            document.SelectionMask = null;
            document.IsDirty = true;

            CommitPixelChange($"Rotate Canvas {degrees}°");
            imageCanvas?.SetDocument(document);
            RefreshLayersPanel();
            UpdateStatusBar();
            UpdateTitle();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Rotate canvas failed: {ex}");
        }
    }

    private void OnFlipActiveLayer(bool horizontal)
    {
        if (document is null) return;
        BeginPixelOperation();
        var layer = document.Layers[document.ActiveLayerIndex];
        if (layer.Content is null) return;
        layer.Content = horizontal
            ? SharpImage.Transform.Geometry.Flop(layer.Content)
            : SharpImage.Transform.Geometry.Flip(layer.Content);
        CommitPixelChange(horizontal ? "Flip Horizontal" : "Flip Vertical");
        imageCanvas?.MarkDirty();
    }

    private void OnRotateActiveLayer(int degrees)
    {
        if (document is null) return;
        var angle = degrees switch
        {
            90 => SharpImage.Transform.RotationAngle.Rotate90,
            180 => SharpImage.Transform.RotationAngle.Rotate180,
            270 => SharpImage.Transform.RotationAngle.Rotate270,
            _ => SharpImage.Transform.RotationAngle.Rotate90,
        };
        BeginPixelOperation();
        var layer = document.Layers[document.ActiveLayerIndex];
        if (layer.Content is null) return;
        layer.Content = SharpImage.Transform.Geometry.Rotate(layer.Content, angle);
        CommitPixelChange($"Rotate {degrees}°");
        imageCanvas?.MarkDirty();
    }

    private void OnCropToSelection()
    {
        if (document is null || !document.HasSelection || document.SelectionMask is null) return;

        // Find bounding box of selection
        int minX = document.Width, minY = document.Height, maxX = 0, maxY = 0;
        var mask = document.SelectionMask;
        for (int y = 0; y < document.Height; y++)
        {
            for (int x = 0; x < document.Width; x++)
            {
                if (mask[y * document.Width + x] > 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX <= minX || maxY <= minY) return;

        int cropW = maxX - minX + 1;
        int cropH = maxY - minY + 1;

        for (int i = 0; i < document.Layers.Count; i++)
        {
            var layer = document.Layers[i];
            if (layer.Content is null) continue;
            var oldFrame = layer.Content;
            var newFrame = new SharpImage.Image.ImageFrame();
            newFrame.Initialize(cropW, cropH, hasAlpha: oldFrame.HasAlpha);
            int channels = newFrame.NumberOfChannels;

            for (int y = 0; y < cropH; y++)
            {
                var srcRow = oldFrame.GetPixelRow(y + minY);
                var dstRow = newFrame.GetPixelRowForWrite(y);
                for (int x = 0; x < cropW; x++)
                {
                    int srcOff = (x + minX) * channels;
                    int dstOff = x * channels;
                    for (int c = 0; c < channels; c++)
                        dstRow[dstOff + c] = srcRow[srcOff + c];
                }
            }
            layer.Content = newFrame;
        }
        document.Resize(cropW, cropH);
        document.SelectionMask = null;
        document.IsDirty = true;
        undoService.Clear();
        imageCanvas?.SetDocument(document);
        RefreshLayersPanel();
        UpdateStatusBar();
        UpdateTitle();
    }

    private SharpImage.Image.ImageFrame GetFilterSource()
    {
        if (filterPreviewSourceOverride is not null) return filterPreviewSourceOverride;
        return document!.GetActiveLayerImage();
    }

    // ═══════ Named filter dialog openers (shared by menus + adjustments panel) ═══════

    private void OpenBrightnessContrast() => ShowFilterDialog("Brightness/Contrast",
        [new() { Label = "Brightness", Min = -100, Max = 100, Default = 0, Step = 1 },
         new() { Label = "Contrast", Min = -100, Max = 100, Default = 0, Step = 1 }],
        v =>
        {
            var frame = GetFilterSource();
            if (Math.Abs(v[0]) > 0.01) frame = SharpImage.Effects.ColorAdjust.Brightness(frame, v[0] / 100.0);
            if (Math.Abs(v[1]) > 0.01) frame = SharpImage.Effects.ColorAdjust.Contrast(frame, 1.0 + v[1] / 100.0);
            return frame;
        });

    private void OpenLevels() => ShowFilterDialog("Levels",
        [new() { Label = "Input Black", Min = 0, Max = 1, Default = 0, Step = 0.01 },
         new() { Label = "Input White", Min = 0, Max = 1, Default = 1, Step = 0.01 },
         new() { Label = "Gamma", Min = 0.1, Max = 5, Default = 1.0, Step = 0.01 },
         new() { Label = "Output Black", Min = 0, Max = 1, Default = 0, Step = 0.01 },
         new() { Label = "Output White", Min = 0, Max = 1, Default = 1, Step = 0.01 }],
        v => SharpImage.Effects.ColorAdjust.Levels(GetFilterSource(), v[0], v[1], v[3], v[4], v[2]));

    private void OpenCurves() => ShowFilterDialog("Curves",
        [new() { Label = "Shadow Point", Min = 0, Max = 0.5, Default = 0, Step = 0.01 },
         new() { Label = "Shadow Output", Min = 0, Max = 1, Default = 0, Step = 0.01 },
         new() { Label = "Midtone Point", Min = 0.2, Max = 0.8, Default = 0.5, Step = 0.01 },
         new() { Label = "Midtone Output", Min = 0, Max = 1, Default = 0.5, Step = 0.01 },
         new() { Label = "Highlight Point", Min = 0.5, Max = 1, Default = 1, Step = 0.01 },
         new() { Label = "Highlight Output", Min = 0, Max = 1, Default = 1, Step = 0.01 }],
        v => SharpImage.Effects.ColorAdjust.Curves(GetFilterSource(),
            [(v[0], v[1]), (v[2], v[3]), (v[4], v[5])]));

    private void OpenHueSaturation() => ShowFilterDialog("Hue/Saturation",
        [new() { Label = "Hue", Min = -180, Max = 180, Default = 0, Step = 1 },
         new() { Label = "Saturation", Min = -100, Max = 100, Default = 0, Step = 1 },
         new() { Label = "Lightness", Min = -100, Max = 100, Default = 0, Step = 1 }],
        v => SharpImage.Enhance.EnhanceOps.Modulate(GetFilterSource(),
            100 + v[2], 100 + v[1], 100 + v[0]));

    private void OpenColorBalance() => ShowFilterDialog("Color Balance",
        [new() { Label = "Cyan ↔ Red", Min = -100, Max = 100, Default = 0, Step = 1 },
         new() { Label = "Magenta ↔ Green", Min = -100, Max = 100, Default = 0, Step = 1 },
         new() { Label = "Yellow ↔ Blue", Min = -100, Max = 100, Default = 0, Step = 1 }],
        v =>
        {
            double rr = 1 + v[0] / 200.0, rg = 0, rb = 0;
            double gr = 0, gg = 1 + v[1] / 200.0, gb = 0;
            double br = 0, bg = 0, bb = 1 + v[2] / 200.0;
            return SharpImage.Effects.ColorGradingOps.ChannelMixer(GetFilterSource(), rr, rg, rb, gr, gg, gb, br, bg, bb);
        });

    private void OpenVibrance() => ShowFilterDialog("Vibrance",
        [new() { Label = "Vibrance", Min = -100, Max = 100, Default = 0, Step = 1 }],
        v => SharpImage.Effects.ColorAdjust.Vibrance(GetFilterSource(), v[0] / 100.0));

    private void OpenExposure() => ShowFilterDialog("Exposure",
        [new() { Label = "Exposure (EV)", Min = -5, Max = 5, Default = 0, Step = 0.1 }],
        v => SharpImage.Effects.ColorAdjust.Exposure(GetFilterSource(), v[0]));

    private void OpenPosterize() => ShowFilterDialog("Posterize",
        [new() { Label = "Levels", Min = 2, Max = 32, Default = 4, IsInteger = true }],
        v => SharpImage.Effects.ColorAdjust.Posterize(GetFilterSource(), (int)v[0]));

    private void OpenThreshold() => ShowFilterDialog("Threshold",
        [new() { Label = "Threshold", Min = 0, Max = 1, Default = 0.5, Step = 0.01 }],
        v => SharpImage.Effects.ColorAdjust.Threshold(GetFilterSource(), v[0]));

    private async void ShowFilterDialog(string title, Dialogs.FilterParam[] parameters,
        Func<double[], SharpImage.Image.ImageFrame> applyFilter,
        Dialogs.FilterComboParam[]? comboParams = null)
    {
        if (document is null) return;
        var dialog = new Dialogs.FilterDialog(title, parameters, comboParams);

        // Wire preview: apply the filter to the real source, then scale for display.
        // This ensures the preview accurately reflects the actual filter behavior.
        var source = GetFilterSource();
        dialog.PreviewSource = source;
        dialog.PreviewApply = (previewFrame, vals, comboVals) =>
        {
            try
            {
                // Temporarily redirect GetFilterSource() to the scaled-down preview frame
                filterPreviewSourceOverride = previewFrame;
                return applyFilter(vals);
            }
            catch
            {
                return previewFrame;
            }
            finally
            {
                filterPreviewSourceOverride = null;
            }
        };
        dialog.ParametersChanged += () => dialog.UpdatePreview();

        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;

        BeginPixelOperation();
        ShowProgress($"Applying {title}...", 0);
        try
        {
            var result = applyFilter(dialog.Values);
            var layer = document.Layers[document.ActiveLayerIndex];
            layer.Content = result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Filter error: {ex.Message}");
            HideProgress();
            return;
        }
        CommitPixelChange(title);
        HideProgress();
        imageCanvas?.MarkDirty();
        RefreshNavigatorPreview();
        UpdateStatusBar();
    }

    private void ApplyInstantFilter(string description, Func<SharpImage.Image.ImageFrame> applyFilter)
    {
        if (document is null) return;
        BeginPixelOperation();
        ShowProgress($"Applying {description}...", 50);
        try
        {
            var result = applyFilter();
            var layer = document.Layers[document.ActiveLayerIndex];
            layer.Content = result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Filter error: {ex.Message}");
            HideProgress();
            return;
        }
        CommitPixelChange(description);
        HideProgress();
        imageCanvas?.MarkDirty();
        UpdateStatusBar();
    }

    private void ApplyGenerateFilter(string description, Func<SharpImage.Image.ImageFrame> generateFunc)
    {
        if (document is null) return;
        BeginPixelOperation();
        try
        {
            var generated = generateFunc();
            var layer = document.Layers[document.ActiveLayerIndex];
            layer.Content = generated;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Generate error: {ex.Message}");
            return;
        }
        CommitPixelChange(description);
        imageCanvas?.MarkDirty();
        UpdateStatusBar();
    }

    private void OnNewLayer()
    {
        if (document is null) return;
        var layer = new SharpImage.Layers.Layer($"Layer {document.Layers.Count + 1}");
        var frame = new SharpImage.Image.ImageFrame();
        frame.Initialize(document.Width, document.Height, hasAlpha: true);
        layer.Content = frame;
        int insertIndex = document.Layers.Count;
        document.Layers.AddLayer(layer);
        document.ActiveLayerIndex = insertIndex;
        document.IsDirty = true;

        undoService.Push(new AddLayerCommand("New Layer", insertIndex, layer));
        imageCanvas?.MarkDirty();
        RefreshLayersPanel();
        UpdateStatusBar();
    }

    private void OnDuplicateLayer()
    {
        if (document is null) return;
        var sourceLayer = document.Layers[document.ActiveLayerIndex];
        var clonedFrame = sourceLayer.Content.Clone();
        var newLayer = new SharpImage.Layers.Layer($"{sourceLayer.Name} copy")
        {
            Content = clonedFrame,
            BlendMode = sourceLayer.BlendMode,
            Opacity = sourceLayer.Opacity,
            Visible = sourceLayer.Visible,
        };
        int insertIndex = document.ActiveLayerIndex + 1;
        document.Layers.InsertLayer(insertIndex, newLayer);
        document.ActiveLayerIndex = insertIndex;
        document.IsDirty = true;

        undoService.Push(new AddLayerCommand("Duplicate Layer", insertIndex, newLayer));
        imageCanvas?.MarkDirty();
        RefreshLayersPanel();
        UpdateStatusBar();
    }

    private void OnDeleteLayer()
    {
        if (document is null || document.Layers.Count <= 1) return;
        int removeIndex = document.ActiveLayerIndex;
        var removedLayer = document.Layers[removeIndex];
        int prevActive = document.ActiveLayerIndex;

        document.Layers.RemoveLayer(removeIndex);
        if (document.ActiveLayerIndex >= document.Layers.Count)
            document.ActiveLayerIndex = document.Layers.Count - 1;
        document.IsDirty = true;

        undoService.Push(new RemoveLayerCommand("Delete Layer", removeIndex, removedLayer, prevActive));
        imageCanvas?.MarkDirty();
        RefreshLayersPanel();
        UpdateStatusBar();
    }

    private void OnResetWorkspace()
    {
        workspaceService.ShowNavigator = true;
        workspaceService.ShowColor = true;
        workspaceService.ShowLayers = true;
        workspaceService.ShowHistory = true;
        workspaceService.ShowProperties = false;
        workspaceService.ShowChannels = false;
        workspaceService.ShowBrushes = false;
        workspaceService.ShowAdjustments = false;
        SyncPanelVisibility();
    }

    private async void OnKeyboardShortcuts()
    {
        var dialog = new Window
        {
            Title = "Keyboard Shortcuts",
            Width = 520, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.FindResource("BgPrimaryBrush") as IBrush,
        };

        var scroll = new ScrollViewer { Padding = new Thickness(20) };
        var stack = new StackPanel { Spacing = 4 };

        string[,] shortcuts =
        {
            { "Ctrl+N", "New Document" }, { "Ctrl+O", "Open" }, { "Ctrl+S", "Save" },
            { "Ctrl+Shift+S", "Save As" }, { "Ctrl+Shift+E", "Export" }, { "Ctrl+W", "Close" },
            { "Ctrl+Z", "Undo" }, { "Ctrl+Y", "Redo" },
            { "Ctrl+X", "Cut" }, { "Ctrl+C", "Copy" }, { "Ctrl+V", "Paste" },
            { "Ctrl+A", "Select All" }, { "Ctrl+D", "Deselect" }, { "Ctrl+Shift+I", "Invert Selection" },
            { "Ctrl+T", "Free Transform" },
            { "Ctrl+L", "Levels" }, { "Ctrl+M", "Curves" }, { "Ctrl+U", "Hue/Saturation" },
            { "Ctrl+B", "Color Balance" }, { "Ctrl+I", "Invert Colors" },
            { "Ctrl+J", "Duplicate Layer" }, { "Ctrl+E", "Merge Down" },
            { "Ctrl+Shift+N", "New Layer" },
            { "Ctrl+0", "Fit in Window" }, { "Ctrl+1", "Actual Pixels" },
            { "Ctrl+=", "Zoom In" }, { "Ctrl+-", "Zoom Out" },
            { "Space (hold)", "Temporary Hand Tool" },
            { "[  /  ]", "Brush Size -/+" }, { "Shift+[  /  ]", "Brush Hardness -/+" },
            { "D", "Reset Colors" }, { "X", "Swap Colors" },
            { "1-9, 0", "Tool Opacity 10%-100%" },
            { "V", "Move" }, { "M", "Marquee" }, { "L", "Lasso" },
            { "W", "Magic Wand" }, { "C", "Crop" }, { "I", "Eyedropper" },
            { "B", "Brush" }, { "E", "Eraser" }, { "G", "Gradient" },
            { "S", "Clone Stamp" }, { "J", "Healing Brush" }, { "O", "Dodge/Burn" },
            { "T", "Text" }, { "H", "Hand" }, { "Z", "Zoom" },
            { "P", "Pen" }, { "U", "Shape" },
        };

        var headerFg = this.FindResource("LabelPrimaryBrush") as IBrush;
        var keyFg = this.FindResource("AccentBlueBrush") as IBrush;
        var descFg = this.FindResource("LabelSecondaryBrush") as IBrush;

        stack.Children.Add(new TextBlock
        {
            Text = "Keyboard Shortcuts",
            FontSize = 18, FontWeight = FontWeight.Bold,
            Foreground = headerFg,
            Margin = new Thickness(0, 0, 0, 12),
        });

        for (int i = 0; i < shortcuts.GetLength(0); i++)
        {
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("150,*"), Margin = new Thickness(0, 2) };
            row.Children.Add(new TextBlock { Text = shortcuts[i, 0], FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = keyFg, VerticalAlignment = VerticalAlignment.Center });
            var desc = new TextBlock { Text = shortcuts[i, 1], FontSize = 12, Foreground = descFg, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(desc, 1);
            row.Children.Add(desc);
            stack.Children.Add(row);
        }

        scroll.Content = stack;
        dialog.Content = scroll;
        await dialog.ShowDialog(this);
    }

    private async void OnAbout()
    {
        var dialog = new Window
        {
            Title = "About SharpImage Editor",
            Width = 360, Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.FindResource("BgPrimaryBrush") as IBrush,
            Content = new StackPanel
            {
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "SharpImage Editor", FontSize = 20, FontWeight = FontWeight.Bold,
                        Foreground = this.FindResource("LabelPrimaryBrush") as IBrush,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                    new TextBlock { Text = "A professional raster image editor", FontSize = 13,
                        Foreground = this.FindResource("LabelSecondaryBrush") as IBrush,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                    new TextBlock { Text = "Powered by SharpImage · .NET 10 · Avalonia", FontSize = 11,
                        Foreground = this.FindResource("LabelTertiaryBrush") as IBrush,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                }
            }
        };
        await dialog.ShowDialog(this);
    }

    // ═══════════════════════════════════════════════════
    //  Status Bar
    // ═══════════════════════════════════════════════════

    private void UpdateStatusBar()
    {
        if (document is not null)
        {
            var dirty = document.IsDirty ? " *" : "";
            StatusFileName.Text = $"{document.Title}{dirty}";
            StatusDimensions.Text = $"{document.Width} × {document.Height}";
        }
        else
        {
            StatusFileName.Text = "No document";
            StatusDimensions.Text = "";
        }
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        if (document is not null)
        {
            var dirty = document.IsDirty ? " *" : "";
            Title = $"{document.Title}{dirty} — SharpImage Editor";
        }
        else
        {
            Title = "SharpImage Editor";
        }
    }

    private void UpdateUndoMenuState()
    {
        MenuUndo.IsEnabled = undoService.CanUndo;
        MenuRedo.IsEnabled = undoService.CanRedo;

        // Show description of what will be undone/redone in the menu header
        if (undoService.CanUndo)
            MenuUndo.Header = $"_Undo {undoService.History[^1].Description}";
        else
            MenuUndo.Header = "_Undo";

        // For redo, we don't have direct access to redo stack description,
        // so just show the generic label with enable/disable
        MenuRedo.Header = "_Redo";
    }

    // ═══════════════════════════════════════════════════
    //  Progress System
    // ═══════════════════════════════════════════════════

    private void ShowProgress(string label, double percent = 0)
    {
        ProgressSection.IsVisible = true;
        ProgressLabel.Text = label;
        ProgressBar.Value = Math.Clamp(percent, 0, 100);
    }

    private void UpdateProgress(double percent)
    {
        ProgressBar.Value = Math.Clamp(percent, 0, 100);
    }

    private void UpdateProgress(string label, double percent)
    {
        ProgressLabel.Text = label;
        ProgressBar.Value = Math.Clamp(percent, 0, 100);
    }

    private void HideProgress()
    {
        ProgressSection.IsVisible = false;
        ProgressLabel.Text = "";
        ProgressBar.Value = 0;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        settingsService.CaptureFrom(workspaceService);
        settingsService.Settings.UndoLevels = undoService.MaxUndoLevels;

        if (WindowState == WindowState.Maximized)
        {
            settingsService.Settings.IsMaximized = true;
        }
        else
        {
            settingsService.Settings.IsMaximized = false;
            settingsService.Settings.WindowWidth = Width;
            settingsService.Settings.WindowHeight = Height;
            settingsService.Settings.WindowX = Position.X;
            settingsService.Settings.WindowY = Position.Y;
        }

        settingsService.Settings.LastOpenFile = document?.FilePath;
        settingsService.Save();
    }
}
