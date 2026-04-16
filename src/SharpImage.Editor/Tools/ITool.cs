using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Contract for all interactive canvas tools. Each tool handles pointer events,
/// renders its own overlays (crop handles, selection rubber bands, brush cursors, etc.),
/// and provides an options bar control for its settings.
/// </summary>
public interface ITool
{
    /// <summary>Display name shown in tooltips and options bar.</summary>
    string Name { get; }

    /// <summary>Resource key for the Lucide icon in Icons.axaml.</summary>
    string IconResourceKey { get; }

    /// <summary>Cursor to display when this tool is active over the canvas.</summary>
    Cursor ToolCursor { get; }

    /// <summary>Called when this tool becomes the active tool.</summary>
    void Activate();

    /// <summary>Called when switching away from this tool.</summary>
    void Deactivate();

    /// <summary>Pointer pressed on the canvas. canvasPoint is in image pixel coordinates.</summary>
    void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint);

    /// <summary>Pointer moved on the canvas.</summary>
    void OnPointerMoved(PointerEventArgs e, Point canvasPoint);

    /// <summary>Pointer released on the canvas.</summary>
    void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint);

    /// <summary>Key pressed while this tool is active.</summary>
    void OnKeyDown(KeyEventArgs e);

    /// <summary>Key released while this tool is active.</summary>
    void OnKeyUp(KeyEventArgs e);

    /// <summary>
    /// Render tool-specific overlays on top of the canvas (crop handles, selection outlines,
    /// brush cursor circle, transform bounding box, etc.). Called during canvas paint.
    /// </summary>
    void RenderOverlay(DrawingContext context, double zoom);

    /// <summary>
    /// Build the options bar UI for this tool (size slider, tolerance spinner, etc.).
    /// Returns a control that will be placed in the options bar area.
    /// </summary>
    Control? BuildOptionsBar();
}
