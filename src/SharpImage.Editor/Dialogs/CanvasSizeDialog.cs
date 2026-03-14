using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace SharpImage.Editor.Dialogs;

/// <summary>
/// Dialog for changing canvas size with anchor position control.
/// </summary>
public sealed class CanvasSizeDialog : Window
{
    private readonly TextBox widthField;
    private readonly TextBox heightField;
    private readonly TextBlock currentSizeLabel;
    private readonly Button[] anchorButtons = new Button[9];

    private readonly int originalWidth;
    private readonly int originalHeight;

    public int NewWidth { get; private set; }
    public int NewHeight { get; private set; }
    public int AnchorX { get; private set; } = 1; // 0=left, 1=center, 2=right
    public int AnchorY { get; private set; } = 1; // 0=top, 1=center, 2=bottom
    public bool Confirmed { get; private set; }

    public CanvasSizeDialog(int currentWidth, int currentHeight)
    {
        originalWidth = currentWidth;
        originalHeight = currentHeight;
        NewWidth = currentWidth;
        NewHeight = currentHeight;

        Title = "Canvas Size";
        Width = 360;
        Height = 350;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("BgPrimaryBrush");

        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(20) };

        var titleText = new TextBlock { Text = "Canvas Size", FontSize = 16, FontWeight = FontWeight.SemiBold };
        titleText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelPrimaryBrush");
        stack.Children.Add(titleText);

        currentSizeLabel = new TextBlock { Text = $"Current: {currentWidth} × {currentHeight} px", FontSize = 11 };
        currentSizeLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LabelSecondaryBrush");
        stack.Children.Add(currentSizeLabel);

        // Width / Height
        var dimRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,16,*"),
            Margin = new Thickness(0, 4),
        };

        widthField = MakeNumericField(currentWidth.ToString());
        heightField = MakeNumericField(currentHeight.ToString());

        var wStack = new StackPanel { Spacing = 2 };
        wStack.Children.Add(new TextBlock { Text = "Width (px):", FontSize = 11 });
        wStack.Children.Add(widthField);
        var hStack = new StackPanel { Spacing = 2 };
        hStack.Children.Add(new TextBlock { Text = "Height (px):", FontSize = 11 });
        hStack.Children.Add(heightField);

        Grid.SetColumn(wStack, 0);
        Grid.SetColumn(hStack, 2);
        dimRow.Children.Add(wStack);
        dimRow.Children.Add(hStack);
        stack.Children.Add(dimRow);

        // Anchor grid (3×3)
        var anchorLabel = new TextBlock { Text = "Anchor:", FontSize = 11, Margin = new Thickness(0, 4, 0, 4) };
        stack.Children.Add(anchorLabel);

        var anchorGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("28,28,28"),
            RowDefinitions = RowDefinitions.Parse("28,28,28"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                int r = row, c = col;
                var btn = new Button
                {
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    Content = (r == 1 && c == 1) ? "●" : "○",
                    FontSize = 10,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                btn.Click += (_, _) => SetAnchor(c, r);
                Grid.SetRow(btn, row);
                Grid.SetColumn(btn, col);
                anchorGrid.Children.Add(btn);
                anchorButtons[row * 3 + col] = btn;
            }
        }
        stack.Children.Add(anchorGrid);

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

        var okBtn = new Button { Content = "Apply", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        okBtn[!Button.BackgroundProperty] = new DynamicResourceExtension("AccentBlueBrush");
        okBtn.Foreground = Avalonia.Media.Brushes.White;
        okBtn.Click += (_, _) => OnApply();

        buttonRow.Children.Add(cancelBtn);
        buttonRow.Children.Add(okBtn);
        stack.Children.Add(buttonRow);

        Content = stack;
    }

    private void SetAnchor(int col, int row)
    {
        AnchorX = col;
        AnchorY = row;
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                anchorButtons[r * 3 + c].Content = (r == row && c == col) ? "●" : "○";
            }
        }
    }

    private void OnApply()
    {
        if (int.TryParse(widthField.Text, out int w) && w > 0 && w <= 65536)
            NewWidth = w;
        if (int.TryParse(heightField.Text, out int h) && h > 0 && h <= 65536)
            NewHeight = h;
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
