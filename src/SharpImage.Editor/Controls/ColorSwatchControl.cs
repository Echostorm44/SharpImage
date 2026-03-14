using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Controls;

/// <summary>
/// Photoshop-style overlapping foreground/background color swatches
/// with swap (X) and reset (D) functionality.
/// </summary>
public sealed class ColorSwatchControl : Panel
{
    private readonly Rectangle foregroundSwatch;
    private readonly Rectangle backgroundSwatch;
    private readonly BrushSettings brushSettings;

    /// <summary>Fires when the user clicks the swap button.</summary>
    public event Action? SwapRequested;

    /// <summary>Fires when the user clicks the reset button.</summary>
    public event Action? ResetRequested;

    public ColorSwatchControl(BrushSettings settings)
    {
        brushSettings = settings;
        Width = 44;
        Height = 44;
        Margin = new Thickness(0, 8, 0, 4);

        // Background swatch (larger, behind, offset bottom-right)
        backgroundSwatch = new Rectangle
        {
            Width = 22,
            Height = 22,
            Fill = new SolidColorBrush(ArgbToColor(settings.BackgroundColor)),
            Stroke = Brushes.Gray,
            StrokeThickness = 1,
            RadiusX = 2,
            RadiusY = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        // Foreground swatch (on top, offset top-left)
        foregroundSwatch = new Rectangle
        {
            Width = 22,
            Height = 22,
            Fill = new SolidColorBrush(ArgbToColor(settings.ForegroundColor)),
            Stroke = Brushes.Gray,
            StrokeThickness = 1,
            RadiusX = 2,
            RadiusY = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        // Swap arrows icon (top-right corner)
        var swapBtn = new TextBlock
        {
            Text = "⇄",
            FontSize = 12,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 0, 0),
        };
        swapBtn.PointerPressed += (_, _) => SwapRequested?.Invoke();

        // Reset icon (bottom-left corner)
        var resetBtn = new TextBlock
        {
            Text = "⬛",
            FontSize = 7,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 0, 0),
        };
        resetBtn.PointerPressed += (_, _) => ResetRequested?.Invoke();

        Children.Add(backgroundSwatch);
        Children.Add(foregroundSwatch);
        Children.Add(swapBtn);
        Children.Add(resetBtn);
    }

    public void UpdateSwatches()
    {
        foregroundSwatch.Fill = new SolidColorBrush(ArgbToColor(brushSettings.ForegroundColor));
        backgroundSwatch.Fill = new SolidColorBrush(ArgbToColor(brushSettings.BackgroundColor));
    }

    public void SetColors(Color foreground, Color background)
    {
        brushSettings.ForegroundColor = ColorToArgb(foreground);
        brushSettings.BackgroundColor = ColorToArgb(background);
        UpdateSwatches();
    }

    private static Color ArgbToColor(uint argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return Color.FromArgb(a, r, g, b);
    }

    private static uint ColorToArgb(Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}
