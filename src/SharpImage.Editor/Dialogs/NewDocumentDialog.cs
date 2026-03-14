using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace SharpImage.Editor.Dialogs;

/// <summary>
/// Dialog for creating a new document with width, height, DPI, and background options.
/// </summary>
public sealed class NewDocumentDialog : Window
{
    private readonly TextBox widthField;
    private readonly TextBox heightField;
    private readonly TextBox dpiField;
    private readonly ComboBox backgroundCombo;
    private readonly ComboBox presetCombo;
    private readonly CheckBox constrainCheck;

    public int DocumentWidth { get; private set; } = 800;
    public int DocumentHeight { get; private set; } = 600;
    public int Dpi { get; private set; } = 72;
    public string BackgroundFill { get; private set; } = "White";
    public bool Confirmed { get; private set; }

    private static readonly (string Name, int W, int H)[] Presets =
    [
        ("Custom", 0, 0),
        ("HD 1280×720", 1280, 720),
        ("Full HD 1920×1080", 1920, 1080),
        ("4K 3840×2160", 3840, 2160),
        ("Instagram Post 1080×1080", 1080, 1080),
        ("Instagram Story 1080×1920", 1080, 1920),
        ("Twitter Post 1200×675", 1200, 675),
        ("A4 Print 300dpi 2480×3508", 2480, 3508),
        ("Letter Print 300dpi 2550×3300", 2550, 3300),
        ("Icon 256×256", 256, 256),
        ("Icon 512×512", 512, 512),
        ("Wallpaper 2560×1440", 2560, 1440),
    ];

    public NewDocumentDialog()
    {
        Title = "New Document";
        Width = 380;
        Height = 340;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("BgPrimaryBrush");

        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(20) };

        // Title
        var titleText = new TextBlock { Text = "New Document", FontSize = 16, FontWeight = FontWeight.SemiBold };
        titleText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        stack.Children.Add(titleText);

        // Preset
        presetCombo = new ComboBox
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = Presets.Select(p => p.Name).ToArray(),
            SelectedIndex = 0,
        };
        presetCombo.SelectionChanged += (_, _) =>
        {
            int idx = presetCombo.SelectedIndex;
            if (idx > 0 && idx < Presets.Length)
            {
                widthField!.Text = Presets[idx].W.ToString();
                heightField!.Text = Presets[idx].H.ToString();
            }
        };
        stack.Children.Add(MakeLabeledRow("Preset:", presetCombo));

        // Width / Height
        var dimGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,8,Auto,8,*"),
            Margin = new Thickness(0, 4),
        };

        widthField = MakeNumericField("800");
        heightField = MakeNumericField("600");
        constrainCheck = new CheckBox { Content = "🔗", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };

        var wLabel = MakeFieldLabel("Width:");
        var hLabel = MakeFieldLabel("Height:");
        var wStack = new StackPanel { Spacing = 2 };
        wStack.Children.Add(wLabel);
        wStack.Children.Add(widthField);
        var hStack = new StackPanel { Spacing = 2 };
        hStack.Children.Add(hLabel);
        hStack.Children.Add(heightField);

        Grid.SetColumn(wStack, 0);
        Grid.SetColumn(constrainCheck, 2);
        Grid.SetColumn(hStack, 4);
        dimGrid.Children.Add(wStack);
        dimGrid.Children.Add(constrainCheck);
        dimGrid.Children.Add(hStack);
        stack.Children.Add(dimGrid);

        // DPI
        dpiField = MakeNumericField("72");
        stack.Children.Add(MakeLabeledRow("Resolution (DPI):", dpiField));

        // Background
        backgroundCombo = new ComboBox
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "White", "Transparent", "Black", "Custom..." },
            SelectedIndex = 0,
        };
        stack.Children.Add(MakeLabeledRow("Background:", backgroundCombo));

        // Buttons
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var cancelBtn = new Button { Content = "Cancel", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancelBtn.Click += (_, _) => Close();

        var okBtn = new Button
        {
            Content = "Create",
            Width = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        okBtn[!Button.BackgroundProperty] = new DynamicResourceExtension("AccentBlueBrush");
        okBtn.Foreground = Avalonia.Media.Brushes.White;
        okBtn.Click += (_, _) => OnCreate();

        buttonRow.Children.Add(cancelBtn);
        buttonRow.Children.Add(okBtn);
        stack.Children.Add(buttonRow);

        Content = stack;
    }

    private void OnCreate()
    {
        if (int.TryParse(widthField.Text, out int w) && w > 0 && w <= 65536)
            DocumentWidth = w;
        if (int.TryParse(heightField.Text, out int h) && h > 0 && h <= 65536)
            DocumentHeight = h;
        if (int.TryParse(dpiField.Text, out int d) && d > 0)
            Dpi = d;
        BackgroundFill = backgroundCombo.SelectedItem?.ToString() ?? "White";
        Confirmed = true;
        Close();
    }

    private static StackPanel MakeLabeledRow(string label, Control control)
    {
        var row = new StackPanel { Spacing = 4 };
        var lbl = new TextBlock { Text = label, FontSize = 11 };
        row.Children.Add(lbl);
        row.Children.Add(control);
        return row;
    }

    private static TextBlock MakeFieldLabel(string text)
    {
        return new TextBlock { Text = text, FontSize = 11 };
    }

    private static TextBox MakeNumericField(string defaultValue)
    {
        return new TextBox
        {
            Text = defaultValue,
            Width = 120,
            FontSize = 12,
            Height = 28,
            Padding = new Thickness(6, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }
}
