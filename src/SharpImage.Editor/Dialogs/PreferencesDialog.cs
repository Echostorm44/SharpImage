using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace SharpImage.Editor.Dialogs;

/// <summary>
/// Dialog for editing application preferences (undo levels, display, performance, theme).
/// </summary>
public sealed class PreferencesDialog : Window
{
    private readonly NumericUpDown undoLevelsField;
    private readonly NumericUpDown recentFilesField;
    private readonly ComboBox checkerboardSizeCombo;
    private readonly ComboBox defaultZoomCombo;
    private readonly Slider maxMemorySlider;
    private readonly TextBlock memoryValueLabel;
    private readonly ComboBox themeCombo;

    public int UndoLevels { get; set; } = 50;
    public int RecentFilesCount { get; private set; } = 10;
    public string CheckerboardSize { get; private set; } = "Medium";
    public string DefaultZoom { get; private set; } = "Fit in Window";
    public int MaxMemoryMb { get; private set; } = 2048;
    public string ThemeChoice { get; private set; } = "System";
    public bool Confirmed { get; private set; }

    public PreferencesDialog()
    {
        Title = "Preferences";
        Width = 460;
        Height = 620;
        CanResize = true;
        MinWidth = 380;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("BgPrimaryBrush");

        var root = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        // Dialog title
        var titleText = new TextBlock { Text = "Preferences", FontSize = 16, FontWeight = FontWeight.SemiBold };
        titleText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        root.Children.Add(titleText);

        // ── General ──
        root.Children.Add(MakeSectionHeader("General"));

        undoLevelsField = new NumericUpDown
        {
            Minimum = 10,
            Maximum = 200,
            Value = 50,
            Increment = 5,
            FormatString = "0",
            FontSize = 12,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        undoLevelsField[!NumericUpDown.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        root.Children.Add(MakeLabeledRow("Undo levels:", undoLevelsField));

        recentFilesField = new NumericUpDown
        {
            Minimum = 5,
            Maximum = 50,
            Value = 10,
            Increment = 1,
            FormatString = "0",
            FontSize = 12,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        recentFilesField[!NumericUpDown.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        root.Children.Add(MakeLabeledRow("Recent files count:", recentFilesField));

        // ── Display ──
        root.Children.Add(MakeSeparator());
        root.Children.Add(MakeSectionHeader("Display"));

        checkerboardSizeCombo = new ComboBox
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Small", "Medium", "Large" },
            SelectedIndex = 1,
        };
        root.Children.Add(MakeLabeledRow("Checkerboard size:", checkerboardSizeCombo));

        defaultZoomCombo = new ComboBox
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Fit in Window", "Actual Pixels" },
            SelectedIndex = 0,
        };
        root.Children.Add(MakeLabeledRow("Default zoom:", defaultZoomCombo));

        // ── Performance ──
        root.Children.Add(MakeSeparator());
        root.Children.Add(MakeSectionHeader("Performance"));

        var memoryHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        var memoryLabel = new TextBlock { Text = "Max memory usage:", FontSize = 11 };
        memoryLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelSecondaryBrush");
        memoryValueLabel = new TextBlock { Text = "2048 MB", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        memoryValueLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        Grid.SetColumn(memoryValueLabel, 1);
        memoryHeader.Children.Add(memoryLabel);
        memoryHeader.Children.Add(memoryValueLabel);

        maxMemorySlider = new Slider
        {
            Minimum = 256,
            Maximum = 8192,
            Value = 2048,
            TickFrequency = 256,
            IsSnapToTickEnabled = true,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        maxMemorySlider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
                memoryValueLabel.Text = $"{(int)maxMemorySlider.Value} MB";
        };

        var memoryStack = new StackPanel { Spacing = 2 };
        memoryStack.Children.Add(memoryHeader);
        memoryStack.Children.Add(maxMemorySlider);
        root.Children.Add(memoryStack);

        // ── Theme ──
        root.Children.Add(MakeSeparator());
        root.Children.Add(MakeSectionHeader("Theme"));

        themeCombo = new ComboBox
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Light", "Dark", "System" },
            SelectedIndex = 2,
        };
        root.Children.Add(MakeLabeledRow("Theme:", themeCombo));

        // ── Buttons ──
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var cancelBtn = new Button { Content = "Cancel", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancelBtn.Click += (_, _) => Close();

        var okBtn = new Button { Content = "OK", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        okBtn[!Button.BackgroundProperty] = new DynamicResourceExtension("AccentBlueBrush");
        okBtn.Foreground = Brushes.White;
        okBtn.Click += (_, _) => OnConfirm();

        buttonRow.Children.Add(cancelBtn);
        buttonRow.Children.Add(okBtn);
        root.Children.Add(buttonRow);

        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    private void OnConfirm()
    {
        UndoLevels = (int)(undoLevelsField.Value ?? 50);
        RecentFilesCount = (int)(recentFilesField.Value ?? 10);
        CheckerboardSize = checkerboardSizeCombo.SelectedItem?.ToString() ?? "Medium";
        DefaultZoom = defaultZoomCombo.SelectedItem?.ToString() ?? "Fit in Window";
        MaxMemoryMb = (int)maxMemorySlider.Value;
        ThemeChoice = themeCombo.SelectedItem?.ToString() ?? "System";
        Confirmed = true;
        Close();
    }

    private TextBlock MakeSectionHeader(string text)
    {
        var header = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold };
        header[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        return header;
    }

    private Border MakeSeparator()
    {
        var separator = new Border { Height = 1, Margin = new Thickness(0, 2), CornerRadius = new CornerRadius(0) };
        separator[!Border.BackgroundProperty] = new DynamicResourceExtension("SeparatorOpaqueBrush");
        return separator;
    }

    private static StackPanel MakeLabeledRow(string label, Control control)
    {
        var row = new StackPanel { Spacing = 4 };
        var lbl = new TextBlock { Text = label, FontSize = 11 };
        lbl[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelSecondaryBrush");
        row.Children.Add(lbl);
        row.Children.Add(control);
        return row;
    }
}
