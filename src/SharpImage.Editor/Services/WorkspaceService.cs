namespace SharpImage.Editor.Services;

/// <summary>
/// Manages which panels are visible and their layout state.
/// Persisted to settings file between sessions.
/// </summary>
public sealed class WorkspaceService
{
    public bool ShowNavigator { get; set; } = true;
    public bool ShowColor { get; set; } = true;
    public bool ShowLayers { get; set; } = true;
    public bool ShowHistory { get; set; } = true;
    public bool ShowProperties { get; set; }
    public bool ShowChannels { get; set; }
    public bool ShowBrushes { get; set; }
    public bool ShowAdjustments { get; set; }

    public bool ShowRulers { get; set; }
    public bool ShowGrid { get; set; }
    public bool ShowGuides { get; set; } = true;
    public bool ShowPixelGrid { get; set; }
    public bool ShowSelectionEdges { get; set; } = true;

    /// <summary>Fired when any panel visibility changes.</summary>
    public event Action? LayoutChanged;

    public void TogglePanel(string panelName)
    {
        switch (panelName)
        {
            case "Navigator":   ShowNavigator = !ShowNavigator; break;
            case "Color":       ShowColor = !ShowColor; break;
            case "Layers":      ShowLayers = !ShowLayers; break;
            case "History":     ShowHistory = !ShowHistory; break;
            case "Properties":  ShowProperties = !ShowProperties; break;
            case "Channels":    ShowChannels = !ShowChannels; break;
            case "Brushes":     ShowBrushes = !ShowBrushes; break;
            case "Adjustments": ShowAdjustments = !ShowAdjustments; break;
        }
        LayoutChanged?.Invoke();
    }
}
