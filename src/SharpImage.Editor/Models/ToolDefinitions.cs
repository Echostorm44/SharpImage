using Avalonia.Input;

namespace SharpImage.Editor.Models;

/// <summary>
/// Enumerates all available tools in the editor toolbox.
/// </summary>
public enum EditorToolType
{
    Move,
    RectangularMarquee,
    EllipticalMarquee,
    Lasso,
    PolygonalLasso,
    MagicWand,
    Crop,
    Eyedropper,
    Brush,
    Eraser,
    PaintBucket,
    Gradient,
    CloneStamp,
    HealingBrush,
    Dodge,
    Burn,
    Sponge,
    BlurBrush,
    SharpenBrush,
    Smudge,
    Text,
    Hand,
    Zoom,
    PenTool,
    Shape,
    Measure,
}

/// <summary>
/// Metadata for a tool: display name, icon resource key, and keyboard shortcut.
/// </summary>
public readonly record struct ToolDefinition(
    EditorToolType Type,
    string DisplayName,
    string IconResourceKey,
    Key Shortcut,
    string? GroupName = null)
{
    /// <summary>
    /// Whether this tool shares a palette slot with other tools (grouped behind a flyout).
    /// </summary>
    public bool IsGrouped => GroupName is not null;
}

/// <summary>
/// Master list of all tool definitions with their shortcuts and icons.
/// </summary>
public static class ToolDefinitions
{
    public static readonly ToolDefinition[] All =
    [
        new(EditorToolType.Move,               "Move",                "IconMousePointer",  Key.V),
        new(EditorToolType.RectangularMarquee, "Rectangular Marquee", "IconSquareDashed",  Key.M, "Marquee"),
        new(EditorToolType.EllipticalMarquee,  "Elliptical Marquee",  "IconCircleDashed",  Key.M, "Marquee"),
        new(EditorToolType.Lasso,              "Lasso",               "IconLasso",         Key.L, "Lasso"),
        new(EditorToolType.PolygonalLasso,     "Polygonal Lasso",     "IconLasso",         Key.L, "Lasso"),
        new(EditorToolType.MagicWand,          "Magic Wand",          "IconWand",          Key.W),
        new(EditorToolType.Crop,               "Crop",                "IconCrop",          Key.C),
        new(EditorToolType.Eyedropper,         "Eyedropper",          "IconPipette",       Key.I),
        new(EditorToolType.Brush,              "Brush",               "IconPaintbrush",    Key.B),
        new(EditorToolType.Eraser,             "Eraser",              "IconEraser",        Key.E),
        new(EditorToolType.PaintBucket,        "Paint Bucket",        "IconPaintBucket",   Key.G, "Fill"),
        new(EditorToolType.Gradient,           "Gradient",            "IconGradient",      Key.G, "Fill"),
        new(EditorToolType.CloneStamp,         "Clone Stamp",         "IconStamp",         Key.S),
        new(EditorToolType.HealingBrush,       "Healing Brush",       "IconBandAid",       Key.J),
        new(EditorToolType.Dodge,              "Dodge",               "IconSun",           Key.O, "DodgeBurn"),
        new(EditorToolType.Burn,               "Burn",                "IconMoon",          Key.O, "DodgeBurn"),
        new(EditorToolType.Sponge,             "Sponge",              "IconDroplets",      Key.O, "DodgeBurn"),
        new(EditorToolType.BlurBrush,          "Blur",                "IconBlur",          Key.R, "Focus"),
        new(EditorToolType.SharpenBrush,       "Sharpen",             "IconSharpen",       Key.R, "Focus"),
        new(EditorToolType.Smudge,             "Smudge",              "IconFingerprint",   Key.R, "Focus"),
        new(EditorToolType.Text,               "Text",                "IconType",          Key.T),
        new(EditorToolType.PenTool,            "Pen",                 "IconPenTool",       Key.P),
        new(EditorToolType.Shape,              "Shape",               "IconSquare",        Key.U),
        new(EditorToolType.Hand,               "Hand",                "IconHand",          Key.H),
        new(EditorToolType.Zoom,               "Zoom",                "IconZoomIn",        Key.Z),
        new(EditorToolType.Measure,            "Measure",             "IconRulerMeasure",  Key.None),
    ];
}
