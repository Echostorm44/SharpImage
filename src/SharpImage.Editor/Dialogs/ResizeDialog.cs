using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace SharpImage.Editor.Dialogs;

/// <summary>
/// Dialog for resizing the image (width, height, interpolation method, constrain proportions).
/// </summary>
public sealed class ResizeDialog : Window
{
    private readonly TextBox widthField;
    private readonly TextBox heightField;
    private readonly ComboBox methodCombo;
    private readonly CheckBox constrainCheck;
    private readonly TextBlock currentSizeLabel;

    private readonly int originalWidth;
    private readonly int originalHeight;

    public int NewWidth { get; private set; }
    public int NewHeight { get; private set; }
    public SharpImage.Transform.InterpolationMethod Method { get; private set; } = SharpImage.Transform.InterpolationMethod.Lanczos3;
    public bool Confirmed { get; private set; }

    private static readonly (string Label, SharpImage.Transform.InterpolationMethod Method)[] Methods =
    [
        ("Nearest Neighbor", SharpImage.Transform.InterpolationMethod.NearestNeighbor),
        ("Bilinear", SharpImage.Transform.InterpolationMethod.Bilinear),
        ("Bicubic", SharpImage.Transform.InterpolationMethod.Bicubic),
        ("Lanczos 3 (Recommended)", SharpImage.Transform.InterpolationMethod.Lanczos3),
        ("Lanczos 2", SharpImage.Transform.InterpolationMethod.Lanczos2),
        ("Lanczos 4", SharpImage.Transform.InterpolationMethod.Lanczos4),
        ("Mitchell", SharpImage.Transform.InterpolationMethod.Mitchell),
        ("Catmull-Rom", SharpImage.Transform.InterpolationMethod.Catrom),
        ("Hermite", SharpImage.Transform.InterpolationMethod.Hermite),
        ("Gaussian", SharpImage.Transform.InterpolationMethod.Gaussian),
        ("Spline", SharpImage.Transform.InterpolationMethod.Spline),
    ];

    private bool updatingFields;

    public ResizeDialog(int currentWidth, int currentHeight)
    {
        originalWidth = currentWidth;
        originalHeight = currentHeight;
        NewWidth = currentWidth;
        NewHeight = currentHeight;

        Title = "Image Size";
        Width = 360;
        Height = 320;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("BgPrimaryBrush");

        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(20) };

        var titleText = new TextBlock { Text = "Image Size", FontSize = 16, FontWeight = FontWeight.SemiBold };
        titleText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        stack.Children.Add(titleText);

        currentSizeLabel = new TextBlock { Text = $"Current: {currentWidth} × {currentHeight} px", FontSize = 11 };
        currentSizeLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelSecondaryBrush");
        stack.Children.Add(currentSizeLabel);

        // Width / Height
        var dimGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,8,Auto,8,*"),
            Margin = new Thickness(0, 4),
        };

        widthField = MakeNumericField(currentWidth.ToString());
        heightField = MakeNumericField(currentHeight.ToString());
        constrainCheck = new CheckBox { Content = "🔗", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };

        widthField.LostFocus += (_, _) => OnWidthChanged();
        heightField.LostFocus += (_, _) => OnHeightChanged();

        var wStack = new StackPanel { Spacing = 2 };
        wStack.Children.Add(new TextBlock { Text = "Width (px):", FontSize = 11 });
        wStack.Children.Add(widthField);
        var hStack = new StackPanel { Spacing = 2 };
        hStack.Children.Add(new TextBlock { Text = "Height (px):", FontSize = 11 });
        hStack.Children.Add(heightField);

        Grid.SetColumn(wStack, 0);
        Grid.SetColumn(constrainCheck, 2);
        Grid.SetColumn(hStack, 4);
        dimGrid.Children.Add(wStack);
        dimGrid.Children.Add(constrainCheck);
        dimGrid.Children.Add(hStack);
        stack.Children.Add(dimGrid);

        // Method
        methodCombo = new ComboBox
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = Methods.Select(m => m.Label).ToArray(),
            SelectedIndex = 3, // Lanczos 3
        };
        var methodStack = new StackPanel { Spacing = 4 };
        methodStack.Children.Add(new TextBlock { Text = "Resample Method:", FontSize = 11 });
        methodStack.Children.Add(methodCombo);
        stack.Children.Add(methodStack);

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

        var okBtn = new Button { Content = "Resize", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        okBtn[!Button.BackgroundProperty] = new DynamicResourceExtension("AccentBlueBrush");
        okBtn.Foreground = Avalonia.Media.Brushes.White;
        okBtn.Click += (_, _) => OnResize();

        buttonRow.Children.Add(cancelBtn);
        buttonRow.Children.Add(okBtn);
        stack.Children.Add(buttonRow);

        Content = stack;
    }

    private void OnWidthChanged()
    {
        if (updatingFields || constrainCheck?.IsChecked != true) return;
        if (!int.TryParse(widthField.Text, out int w) || w <= 0) return;
        updatingFields = true;
        double ratio = (double)originalHeight / originalWidth;
        heightField.Text = Math.Max(1, (int)Math.Round(w * ratio)).ToString();
        updatingFields = false;
    }

    private void OnHeightChanged()
    {
        if (updatingFields || constrainCheck?.IsChecked != true) return;
        if (!int.TryParse(heightField.Text, out int h) || h <= 0) return;
        updatingFields = true;
        double ratio = (double)originalWidth / originalHeight;
        widthField.Text = Math.Max(1, (int)Math.Round(h * ratio)).ToString();
        updatingFields = false;
    }

    private void OnResize()
    {
        if (int.TryParse(widthField.Text, out int w) && w > 0 && w <= 65536)
            NewWidth = w;
        if (int.TryParse(heightField.Text, out int h) && h > 0 && h <= 65536)
            NewHeight = h;
        int methodIdx = methodCombo.SelectedIndex;
        if (methodIdx >= 0 && methodIdx < Methods.Length)
            Method = Methods[methodIdx].Method;
        Confirmed = true;
        Close();
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
