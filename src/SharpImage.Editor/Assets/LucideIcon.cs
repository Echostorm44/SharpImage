using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvPath = Avalonia.Controls.Shapes.Path;

namespace SharpImage.Editor.Assets;

/// <summary>
/// Helper for creating Lucide-style icon controls from SVG path data.
/// All Lucide icons use a 24×24 viewbox with stroke-based rendering.
/// </summary>
public static class LucideIcon
{
    /// <summary>
    /// Creates a Viewbox containing a stroked Path for use as an icon.
    /// </summary>
    public static Viewbox Create(string pathData, double size = 18, IBrush? stroke = null)
    {
        var iconPath = new AvPath
        {
            Data = Geometry.Parse(pathData),
            Stroke = stroke ?? Brushes.White,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };

        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children = { iconPath }
            }
        };
    }

    /// <summary>
    /// Creates a Viewbox from an Icons.axaml resource key. Binds stroke to theme color.
    /// </summary>
    public static Viewbox CreateFromResource(Control owner, string resourceKey, double size = 18)
    {
        var pathData = owner.FindResource(resourceKey) as string ?? "";
        var iconPath = new AvPath
        {
            Data = Geometry.Parse(pathData),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };

        iconPath[!AvPath.StrokeProperty] = owner.GetResourceObservable("LabelPrimaryBrush").ToBinding();

        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children = { iconPath }
            }
        };
    }
}
