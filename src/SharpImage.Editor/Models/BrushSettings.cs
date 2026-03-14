namespace SharpImage.Editor.Models;

/// <summary>
/// Settings for painting tools (brush, eraser, clone stamp, etc.)
/// </summary>
public sealed class BrushSettings
{
    /// <summary>Brush diameter in pixels (1–5000).</summary>
    public double Size { get; set; } = 20;

    /// <summary>Brush edge hardness (0.0 = fully soft, 1.0 = fully hard).</summary>
    public double Hardness { get; set; } = 1.0;

    /// <summary>Overall opacity for the stroke (0.0–1.0).</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>Flow rate — how fast paint builds up per dab (0.0–1.0).</summary>
    public double Flow { get; set; } = 1.0;

    /// <summary>Spacing between dabs as a fraction of brush size (0.01–2.0).</summary>
    public double Spacing { get; set; } = 0.25;

    /// <summary>Foreground color for painting (ARGB packed).</summary>
    public uint ForegroundColor { get; set; } = 0xFF000000; // Black

    /// <summary>Background color (ARGB packed).</summary>
    public uint BackgroundColor { get; set; } = 0xFFFFFFFF; // White

    /// <summary>Swap foreground and background colors.</summary>
    public void SwapColors()
    {
        (ForegroundColor, BackgroundColor) = (BackgroundColor, ForegroundColor);
    }

    /// <summary>Reset to default black foreground, white background.</summary>
    public void ResetColors()
    {
        ForegroundColor = 0xFF000000;
        BackgroundColor = 0xFFFFFFFF;
    }
}
