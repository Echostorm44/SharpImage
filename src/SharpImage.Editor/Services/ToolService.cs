using SharpImage.Editor.Models;

namespace SharpImage.Editor.Services;

/// <summary>
/// Tracks the currently active tool, brush settings, and foreground/background colors.
/// Central state for tool switching and tool option bar updates.
/// </summary>
public sealed class ToolService
{
    private EditorToolType activeToolType = EditorToolType.Move;

    public EditorToolType ActiveToolType
    {
        get => activeToolType;
        set
        {
            if (activeToolType == value) return;
            activeToolType = value;
            ActiveToolChanged?.Invoke(value);
        }
    }

    public BrushSettings Brush { get; } = new();

    /// <summary>Fired when the active tool changes. UI should update cursor, options bar, etc.</summary>
    public event Action<EditorToolType>? ActiveToolChanged;

    /// <summary>
    /// Finds the ToolDefinition for the current active tool.
    /// </summary>
    public ToolDefinition GetActiveDefinition()
    {
        foreach (var def in ToolDefinitions.All)
        {
            if (def.Type == activeToolType) return def;
        }
        return ToolDefinitions.All[0];
    }

    /// <summary>
    /// Handles a tool shortcut key press. If the same key is pressed again,
    /// cycles through grouped tools sharing that shortcut.
    /// </summary>
    public void HandleShortcut(Avalonia.Input.Key key)
    {
        // Find all tools matching this shortcut
        var matches = new List<ToolDefinition>();
        foreach (var def in ToolDefinitions.All)
        {
            if (def.Shortcut == key) matches.Add(def);
        }

        if (matches.Count == 0) return;

        // If current tool is one of the matches, cycle to next
        int currentIndex = -1;
        for (int i = 0; i < matches.Count; i++)
        {
            if (matches[i].Type == activeToolType) { currentIndex = i; break; }
        }

        int nextIndex = (currentIndex + 1) % matches.Count;
        ActiveToolType = matches[nextIndex].Type;
    }
}
