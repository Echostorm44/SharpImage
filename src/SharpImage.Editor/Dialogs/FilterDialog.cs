using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using SharpImage.Image;

namespace SharpImage.Editor.Dialogs;

/// <summary>
/// Describes a single parameter for a filter dialog.
/// </summary>
public sealed class FilterParam
{
    public string Label { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Default { get; init; } = 50;
    public double Step { get; init; } = 1;
    public bool IsInteger { get; init; }
}

/// <summary>
/// Describes a combo box parameter for a filter dialog.
/// </summary>
public sealed class FilterComboParam
{
    public string Label { get; init; } = "";
    public string[] Options { get; init; } = [];
    public int DefaultIndex { get; init; }
}

/// <summary>
/// Reusable filter dialog with parameter sliders, live preview, and OK/Cancel.
/// </summary>
public sealed class FilterDialog : Window
{
    private readonly FilterParam[] parameters;
    private readonly FilterComboParam[] comboParameters;
    private readonly Slider[] sliders;
    private readonly TextBlock[] valueLabels;
    private readonly ComboBox[] combos;
    private readonly CheckBox previewCheck;
    private readonly Avalonia.Controls.Image? previewImage;
    private Avalonia.Media.Imaging.WriteableBitmap? previewBitmap;

    /// <summary>Source image for generating preview (set by caller before showing).</summary>
    public ImageFrame? PreviewSource { get; set; }

    /// <summary>Called to apply the filter to a preview frame. Set by caller.</summary>
    public Func<ImageFrame, double[], int[], ImageFrame>? PreviewApply { get; set; }

    public double[] Values { get; }
    public int[] ComboValues { get; }
    public bool Confirmed { get; private set; }

    /// <summary>
    /// Raised when a parameter changes and preview is enabled.
    /// </summary>
    public event Action? ParametersChanged;

    public FilterDialog(string title, FilterParam[] filterParams, FilterComboParam[]? comboParams = null)
    {
        parameters = filterParams;
        comboParameters = comboParams ?? [];
        sliders = new Slider[parameters.Length];
        valueLabels = new TextBlock[parameters.Length];
        combos = new ComboBox[comboParameters.Length];
        Values = new double[parameters.Length];
        ComboValues = new int[comboParameters.Length];

        for (int i = 0; i < parameters.Length; i++)
            Values[i] = parameters[i].Default;
        for (int i = 0; i < comboParameters.Length; i++)
            ComboValues[i] = comboParameters[i].DefaultIndex;

        Title = title;
        Width = 440;
        int sliderCount = parameters.Length + (comboParams?.Length ?? 0);
        Height = Math.Max(420, 200 + sliderCount * 56 + 100);
        CanResize = true;
        MinWidth = 380;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("BgPrimaryBrush");

        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(20) };

        // Title
        var titleText = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold };
        titleText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        stack.Children.Add(titleText);

        // Preview area — smaller for dialogs with many parameters
        int previewHeight = sliderCount > 4 ? 120 : 160;
        var previewBorder = new Border
        {
            Height = previewHeight,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Margin = new Thickness(0, 4),
        };
        previewBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("BgCanvasBrush");
        previewImage = new Avalonia.Controls.Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        previewBorder.Child = previewImage;
        stack.Children.Add(previewBorder);

        // Combo parameters
        for (int i = 0; i < comboParameters.Length; i++)
        {
            int idx = i;
            var cp = comboParameters[i];
            var row = new StackPanel { Spacing = 2 };
            var label = new TextBlock { Text = cp.Label, FontSize = 11 };
            label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelSecondaryBrush");
            var combo = new ComboBox
            {
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = cp.Options,
                SelectedIndex = cp.DefaultIndex,
            };
            combo.SelectionChanged += (_, _) =>
            {
                ComboValues[idx] = combo.SelectedIndex;
                if (previewCheck?.IsChecked == true) ParametersChanged?.Invoke();
            };
            combos[i] = combo;
            row.Children.Add(label);
            row.Children.Add(combo);
            stack.Children.Add(row);
        }

        // Slider parameters
        for (int i = 0; i < parameters.Length; i++)
        {
            int idx = i;
            var p = parameters[i];

            var headerRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            };
            var label = new TextBlock { Text = p.Label, FontSize = 11 };
            label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelSecondaryBrush");
            var valLabel = new TextBlock
            {
                Text = FormatValue(p.Default, p.IsInteger),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            valLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
            Grid.SetColumn(valLabel, 1);
            headerRow.Children.Add(label);
            headerRow.Children.Add(valLabel);

            var slider = new Slider
            {
                Minimum = p.Min,
                Maximum = p.Max,
                Value = p.Default,
                TickFrequency = p.Step,
                IsSnapToTickEnabled = p.IsInteger,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            slider.PropertyChanged += (_, args) =>
            {
                if (args.Property == Slider.ValueProperty)
                {
                    double val = slider.Value;
                    if (p.IsInteger) val = Math.Round(val);
                    Values[idx] = val;
                    valLabel.Text = FormatValue(val, p.IsInteger);
                    if (previewCheck?.IsChecked == true) ParametersChanged?.Invoke();
                }
            };

            sliders[i] = slider;
            valueLabels[i] = valLabel;

            var paramStack = new StackPanel { Spacing = 2 };
            paramStack.Children.Add(headerRow);
            paramStack.Children.Add(slider);
            stack.Children.Add(paramStack);
        }

        // Preview checkbox
        previewCheck = new CheckBox { Content = "Preview", IsChecked = true, FontSize = 11, Margin = new Thickness(0, 4) };
        previewCheck[!Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        previewCheck.IsCheckedChanged += (_, _) => UpdatePreview();
        stack.Children.Add(previewCheck);

        // Buttons
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var resetBtn = new Button { Content = "Reset", Width = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
        resetBtn.Click += (_, _) => ResetDefaults();

        var cancelBtn = new Button { Content = "Cancel", Width = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancelBtn.Click += (_, _) => Close();

        var okBtn = new Button { Content = "OK", Width = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
        okBtn[!Button.BackgroundProperty] = new DynamicResourceExtension("AccentBlueBrush");
        okBtn.Foreground = Brushes.White;
        okBtn.Click += (_, _) => { Confirmed = true; Close(); };

        buttonRow.Children.Add(resetBtn);
        buttonRow.Children.Add(cancelBtn);
        buttonRow.Children.Add(okBtn);
        stack.Children.Add(buttonRow);

        Content = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        // Initial preview update after dialog shown
        Opened += (_, _) => UpdatePreview();
    }

    /// <summary>
    /// Renders a preview of the filter with current parameter values.
    /// </summary>
    public void UpdatePreview()
    {
        if (previewImage is null || PreviewSource is null || PreviewApply is null) return;
        if (previewCheck?.IsChecked != true)
        {
            RenderPreviewFromFrame(PreviewSource);
            return;
        }

        try
        {
            // Apply filter to a scaled-down copy for speed
            int srcW = (int)PreviewSource.Columns;
            int srcH = (int)PreviewSource.Rows;
            const int maxPreview = 300;
            double scale = Math.Min((double)maxPreview / srcW, (double)maxPreview / srcH);
            scale = Math.Min(scale, 1.0);
            int pw = Math.Max(1, (int)(srcW * scale));
            int ph = Math.Max(1, (int)(srcH * scale));

            var small = scale < 1.0
                ? SharpImage.Transform.Resize.Apply(PreviewSource, pw, ph, SharpImage.Transform.InterpolationMethod.Bilinear)
                : PreviewSource;

            var result = PreviewApply(small, Values, ComboValues);
            RenderPreviewFromFrame(result);
        }
        catch
        {
            // Ignore preview failures
        }
    }

    private void RenderPreviewFromFrame(ImageFrame frame)
    {
        if (previewImage is null) return;
        int w = (int)frame.Columns;
        int h = (int)frame.Rows;
        if (w == 0 || h == 0) return;

        var size = new Avalonia.PixelSize(w, h);
        if (previewBitmap is null || previewBitmap.PixelSize != size)
        {
            previewBitmap?.Dispose();
            previewBitmap = new Avalonia.Media.Imaging.WriteableBitmap(size, new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        }

        using var fb = previewBitmap.Lock();
        int channels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;

        unsafe
        {
            byte* dst = (byte*)fb.Address;
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRow(y);
                for (int x = 0; x < w; x++)
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

                    int dstIdx = (y * fb.RowBytes) + x * 4;
                    dst[dstIdx + 0] = (byte)(b * a / 255);
                    dst[dstIdx + 1] = (byte)(g * a / 255);
                    dst[dstIdx + 2] = (byte)(r * a / 255);
                    dst[dstIdx + 3] = a;
                }
            }
        }

        previewImage.Source = previewBitmap;
    }

    private void ResetDefaults()
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            sliders[i].Value = parameters[i].Default;
            Values[i] = parameters[i].Default;
        }
        for (int i = 0; i < comboParameters.Length; i++)
        {
            combos[i].SelectedIndex = comboParameters[i].DefaultIndex;
            ComboValues[i] = comboParameters[i].DefaultIndex;
        }
    }

    private static string FormatValue(double val, bool isInt) =>
        isInt ? ((int)val).ToString() : val.ToString("F1");
}
