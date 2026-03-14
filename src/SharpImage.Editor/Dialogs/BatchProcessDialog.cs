using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;

namespace SharpImage.Editor.Dialogs;

/// <summary>
/// Dialog for batch processing image files — collects settings, shows progress,
/// and exposes controls so the caller can drive the actual processing.
/// </summary>
public sealed class BatchProcessDialog : Window
{
    // Source
    private readonly TextBlock sourceFolderText;
    private readonly CheckBox recursiveCheck;

    // Resize
    private readonly CheckBox resizeCheck;
    private readonly TextBox resizeWidthField;
    private readonly TextBox resizeHeightField;
    private readonly CheckBox constrainCheck;

    // Convert
    private readonly CheckBox convertCheck;
    private readonly ComboBox formatCombo;

    // Quality
    private readonly StackPanel qualityPanel;
    private readonly Slider qualitySlider;
    private readonly TextBlock qualityValueLabel;

    // Filter
    private readonly CheckBox filterCheck;
    private readonly ComboBox filterCombo;

    // Destination
    private readonly TextBlock outputFolderText;
    private readonly ComboBox namingCombo;
    private readonly TextBox namingTextField;

    // Progress
    private readonly StackPanel progressPanel;
    private readonly TextBlock statusTextBlock;
    private readonly ProgressBar progressBar;
    private readonly TextBlock fileCountText;

    // Buttons
    private readonly Button startButton;
    private readonly Button cancelButton;

    private static readonly string[] Formats = ["PNG", "JPEG", "WebP", "BMP", "TIFF", "QOI"];
    private static readonly string[] Filters = ["Sharpen", "Auto Levels", "Desaturate", "Auto Contrast"];
    private static readonly string[] NamingModes = ["Keep Original", "Add Prefix", "Add Suffix"];

    // Public properties for reading settings
    public string SourceFolder { get; private set; } = string.Empty;
    public string OutputFolder { get; private set; } = string.Empty;
    public bool IsRecursive => recursiveCheck.IsChecked == true;
    public bool ResizeEnabled => resizeCheck.IsChecked == true;
    public int ResizeWidth => int.TryParse(resizeWidthField.Text, out int w) && w > 0 ? w : 0;
    public int ResizeHeight => int.TryParse(resizeHeightField.Text, out int h) && h > 0 ? h : 0;
    public bool ConstrainProportions => constrainCheck.IsChecked == true;
    public bool ConvertEnabled => convertCheck.IsChecked == true;
    public string TargetFormat => formatCombo.SelectedItem?.ToString() ?? "PNG";
    public int Quality => (int)qualitySlider.Value;
    public bool FilterEnabled => filterCheck.IsChecked == true;
    public string FilterName => filterCombo.SelectedItem?.ToString() ?? "Sharpen";
    public string NamingMode => namingCombo.SelectedItem?.ToString() ?? "Keep Original";
    public string NamingText => namingTextField.Text ?? string.Empty;
    public bool Confirmed { get; private set; }

    /// <summary>The progress bar — caller updates Value (0–100) during processing.</summary>
    public ProgressBar BatchProgressBar => progressBar;

    /// <summary>The status text — caller sets Text during processing.</summary>
    public TextBlock StatusText => statusTextBlock;

    /// <summary>The file count label — caller sets Text (e.g. "3 / 42").</summary>
    public TextBlock FileCountText => fileCountText;

    /// <summary>Raised when the user clicks Start Processing.</summary>
    public event EventHandler? StartProcessing;

    public BatchProcessDialog()
    {
        Title = "Batch Process";
        Width = 480;
        Height = 620;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("BgPrimaryBrush");

        var root = new StackPanel { Spacing = 10, Margin = new Thickness(20) };

        // Title
        var titleText = new TextBlock { Text = "Batch Process", FontSize = 16, FontWeight = FontWeight.SemiBold };
        titleText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        root.Children.Add(titleText);

        // ── Source ──
        root.Children.Add(MakeSectionHeader("Source"));

        var sourceRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,8,Auto") };
        sourceFolderText = new TextBlock
        {
            Text = "(no folder selected)",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        sourceFolderText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelSecondaryBrush");
        var sourceBrowse = new Button { Content = "Browse…", FontSize = 11, Padding = new Thickness(8, 4) };
        sourceBrowse.Click += async (_, _) => await PickFolder(true);
        Grid.SetColumn(sourceFolderText, 0);
        Grid.SetColumn(sourceBrowse, 2);
        sourceRow.Children.Add(sourceFolderText);
        sourceRow.Children.Add(sourceBrowse);
        root.Children.Add(sourceRow);

        recursiveCheck = new CheckBox { Content = "Include subdirectories", FontSize = 12, IsChecked = false };
        root.Children.Add(recursiveCheck);

        root.Children.Add(MakeSeparator());

        // ── Operations ──
        root.Children.Add(MakeSectionHeader("Operations"));

        // Resize
        resizeCheck = new CheckBox { Content = "Resize", FontSize = 12 };
        var resizeDimGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,4,60,12,Auto,4,60,12,Auto"),
            Margin = new Thickness(24, 4, 0, 0),
        };
        var wLabel = MakeSmallLabel("W:");
        resizeWidthField = MakeNumericField("1920");
        var hLabel = MakeSmallLabel("H:");
        resizeHeightField = MakeNumericField("1080");
        constrainCheck = new CheckBox { Content = "Constrain", FontSize = 11, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

        Grid.SetColumn(wLabel, 0); Grid.SetColumn(resizeWidthField, 2);
        Grid.SetColumn(hLabel, 4); Grid.SetColumn(resizeHeightField, 6);
        Grid.SetColumn(constrainCheck, 8);
        resizeDimGrid.Children.Add(wLabel);
        resizeDimGrid.Children.Add(resizeWidthField);
        resizeDimGrid.Children.Add(hLabel);
        resizeDimGrid.Children.Add(resizeHeightField);
        resizeDimGrid.Children.Add(constrainCheck);

        root.Children.Add(resizeCheck);
        root.Children.Add(resizeDimGrid);

        // Convert format
        var convertRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,12,*"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        convertCheck = new CheckBox { Content = "Convert Format", FontSize = 12 };
        formatCombo = new ComboBox
        {
            FontSize = 12,
            ItemsSource = Formats,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        formatCombo.SelectionChanged += (_, _) => UpdateQualityVisibility();
        Grid.SetColumn(convertCheck, 0);
        Grid.SetColumn(formatCombo, 2);
        convertRow.Children.Add(convertCheck);
        convertRow.Children.Add(formatCombo);
        root.Children.Add(convertRow);

        // Quality slider (only for JPEG/WebP)
        qualityPanel = new StackPanel { Spacing = 4, Margin = new Thickness(24, 4, 0, 0) };
        var qualityRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,8,*,8,Auto") };
        var qualityLabel = MakeSmallLabel("Quality:");
        qualitySlider = new Slider { Minimum = 1, Maximum = 100, Value = 90, HorizontalAlignment = HorizontalAlignment.Stretch };
        qualityValueLabel = new TextBlock { Text = "90", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        qualityValueLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        qualitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
                qualityValueLabel.Text = ((int)qualitySlider.Value).ToString();
        };
        Grid.SetColumn(qualityLabel, 0);
        Grid.SetColumn(qualitySlider, 2);
        Grid.SetColumn(qualityValueLabel, 4);
        qualityRow.Children.Add(qualityLabel);
        qualityRow.Children.Add(qualitySlider);
        qualityRow.Children.Add(qualityValueLabel);
        qualityPanel.Children.Add(qualityRow);
        qualityPanel.IsVisible = false;
        root.Children.Add(qualityPanel);

        // Filter
        var filterRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,12,*"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        filterCheck = new CheckBox { Content = "Apply Filter", FontSize = 12 };
        filterCombo = new ComboBox
        {
            FontSize = 12,
            ItemsSource = Filters,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(filterCheck, 0);
        Grid.SetColumn(filterCombo, 2);
        filterRow.Children.Add(filterCheck);
        filterRow.Children.Add(filterCombo);
        root.Children.Add(filterRow);

        root.Children.Add(MakeSeparator());

        // ── Destination ──
        root.Children.Add(MakeSectionHeader("Destination"));

        var destRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,8,Auto") };
        outputFolderText = new TextBlock
        {
            Text = "(no folder selected)",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        outputFolderText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelSecondaryBrush");
        var destBrowse = new Button { Content = "Browse…", FontSize = 11, Padding = new Thickness(8, 4) };
        destBrowse.Click += async (_, _) => await PickFolder(false);
        Grid.SetColumn(outputFolderText, 0);
        Grid.SetColumn(destBrowse, 2);
        destRow.Children.Add(outputFolderText);
        destRow.Children.Add(destBrowse);
        root.Children.Add(destRow);

        // Naming
        var namingRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,8,*"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        namingCombo = new ComboBox
        {
            FontSize = 12,
            ItemsSource = NamingModes,
            SelectedIndex = 0,
            Width = 140,
        };
        namingTextField = new TextBox
        {
            Text = "",
            FontSize = 12,
            Height = 28,
            Padding = new Thickness(6, 3),
            Watermark = "prefix / suffix",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = false,
        };
        namingCombo.SelectionChanged += (_, _) =>
        {
            namingTextField.IsVisible = namingCombo.SelectedIndex > 0;
        };
        Grid.SetColumn(namingCombo, 0);
        Grid.SetColumn(namingTextField, 2);
        namingRow.Children.Add(namingCombo);
        namingRow.Children.Add(namingTextField);
        var namingStack = new StackPanel { Spacing = 4 };
        namingStack.Children.Add(MakeSmallLabel("File Naming:"));
        namingStack.Children.Add(namingRow);
        root.Children.Add(namingStack);

        root.Children.Add(MakeSeparator());

        // ── Progress ──
        progressPanel = new StackPanel { Spacing = 6, IsVisible = false };
        statusTextBlock = new TextBlock { Text = "Preparing…", FontSize = 12 };
        statusTextBlock[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 16, CornerRadius = new CornerRadius(4) };
        fileCountText = new TextBlock { Text = "0 / 0", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        fileCountText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelSecondaryBrush");
        progressPanel.Children.Add(statusTextBlock);
        progressPanel.Children.Add(progressBar);
        progressPanel.Children.Add(fileCountText);
        root.Children.Add(progressPanel);

        // ── Buttons ──
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        cancelButton = new Button { Content = "Cancel", Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancelButton.Click += (_, _) => Close();

        startButton = new Button
        {
            Content = "Start Processing",
            Width = 130,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        startButton[!Button.BackgroundProperty] = new DynamicResourceExtension("AccentBlueBrush");
        startButton.Foreground = Avalonia.Media.Brushes.White;
        startButton.Click += (_, _) => OnStartClicked();

        buttonRow.Children.Add(cancelButton);
        buttonRow.Children.Add(startButton);
        root.Children.Add(buttonRow);

        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>Shows the progress section and disables the start button.</summary>
    public void ShowProgress()
    {
        progressPanel.IsVisible = true;
        startButton.IsEnabled = false;
    }

    /// <summary>Re-enables the start button (call after processing completes/fails).</summary>
    public void ProcessingComplete()
    {
        startButton.IsEnabled = true;
        startButton.Content = "Start Processing";
    }

    private void OnStartClicked()
    {
        if (string.IsNullOrWhiteSpace(SourceFolder) || string.IsNullOrWhiteSpace(OutputFolder))
            return;

        Confirmed = true;
        ShowProgress();
        StartProcessing?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateQualityVisibility()
    {
        string fmt = formatCombo.SelectedItem?.ToString() ?? "";
        qualityPanel.IsVisible = fmt is "JPEG" or "WebP";
    }

    private async Task PickFolder(bool isSource)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = isSource ? "Select Source Folder" : "Select Output Folder",
            AllowMultiple = false,
        });

        if (result.Count > 0)
        {
            string path = result[0].Path.LocalPath;
            if (isSource)
            {
                SourceFolder = path;
                sourceFolderText.Text = path;
            }
            else
            {
                OutputFolder = path;
                outputFolderText.Text = path;
            }
        }
    }

    private static TextBlock MakeSectionHeader(string text)
    {
        var header = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        header[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        return header;
    }

    private static Border MakeSeparator()
    {
        var sep = new Border { Height = 1, Margin = new Thickness(0, 4), CornerRadius = new CornerRadius(0) };
        sep[!Border.BackgroundProperty] = new DynamicResourceExtension("SeparatorOpaqueBrush");
        return sep;
    }

    private static TextBlock MakeSmallLabel(string text)
    {
        var lbl = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        lbl[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelSecondaryBrush");
        return lbl;
    }

    private static TextBox MakeNumericField(string defaultValue)
    {
        return new TextBox
        {
            Text = defaultValue,
            Width = 60,
            FontSize = 12,
            Height = 28,
            Padding = new Thickness(6, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }
}
