using SharpImage.Editor.Models;
using SharpImage.Image;
using SharpImage.Layers;

namespace SharpImage.Editor.Services;

/// <summary>
/// Manages the undo/redo history for an editor document using a command pattern.
/// Supports pixel-level snapshots (ImageFrame clones) for paint/filter operations
/// and lightweight structural commands for layer add/remove/reorder.
/// </summary>
public sealed class UndoService
{
    private readonly List<UndoCommand> undoStack = [];
    private readonly List<UndoCommand> redoStack = [];

    /// <summary>Maximum undo states before discarding oldest.</summary>
    public int MaxUndoLevels { get; set; } = 50;

    public bool CanUndo => undoStack.Count > 0;
    public bool CanRedo => redoStack.Count > 0;
    public int UndoCount => undoStack.Count;
    public int RedoCount => redoStack.Count;

    /// <summary>Fired when the undo/redo state changes (for history panel + menu updates).</summary>
    public event Action? StateChanged;

    /// <summary>All undo commands for display in the history panel (oldest first).</summary>
    public IReadOnlyList<UndoCommand> History => undoStack;

    /// <summary>
    /// Push a completed command onto the undo stack. Clears the redo stack.
    /// </summary>
    public void Push(UndoCommand command)
    {
        while (undoStack.Count >= MaxUndoLevels)
            undoStack.RemoveAt(0);

        undoStack.Add(command);
        redoStack.Clear();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Undo the most recent command, applying it to the given document.
    /// Returns the command that was undone (null if nothing to undo).
    /// </summary>
    public UndoCommand? Undo(EditorDocument document)
    {
        if (undoStack.Count == 0) return null;

        var command = undoStack[^1];
        undoStack.RemoveAt(undoStack.Count - 1);
        command.Undo(document);
        redoStack.Add(command);
        StateChanged?.Invoke();
        return command;
    }

    /// <summary>
    /// Redo the most recently undone command.
    /// Returns the command that was redone (null if nothing to redo).
    /// </summary>
    public UndoCommand? Redo(EditorDocument document)
    {
        if (redoStack.Count == 0) return null;

        var command = redoStack[^1];
        redoStack.RemoveAt(redoStack.Count - 1);
        command.Redo(document);
        undoStack.Add(command);
        StateChanged?.Invoke();
        return command;
    }

    /// <summary>
    /// Jump to a specific point in history by index (0 = before first action).
    /// Used when clicking a history panel entry.
    /// </summary>
    public void JumpToState(int targetIndex, EditorDocument document)
    {
        // targetIndex is the index in undoStack of the state we want to be AT
        // -1 means "before any action" (undo everything)
        while (undoStack.Count > targetIndex + 1 && undoStack.Count > 0)
        {
            var cmd = undoStack[^1];
            undoStack.RemoveAt(undoStack.Count - 1);
            cmd.Undo(document);
            redoStack.Add(cmd);
        }

        while (undoStack.Count < targetIndex + 1 && redoStack.Count > 0)
        {
            var cmd = redoStack[^1];
            redoStack.RemoveAt(redoStack.Count - 1);
            cmd.Redo(document);
            undoStack.Add(cmd);
        }

        StateChanged?.Invoke();
    }

    /// <summary>Clear all undo/redo history (e.g., when closing a document).</summary>
    public void Clear()
    {
        undoStack.Clear();
        redoStack.Clear();
        StateChanged?.Invoke();
    }
}

// ═══════════════════════════════════════════════════════════
//  Undo Command Base & Concrete Commands
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Base class for all undoable operations.
/// </summary>
public abstract class UndoCommand
{
    public string Description { get; }

    protected UndoCommand(string description) => Description = description;

    public abstract void Undo(EditorDocument document);
    public abstract void Redo(EditorDocument document);
}

/// <summary>
/// Captures a full ImageFrame snapshot for a single layer before a pixel-modifying operation
/// (brush stroke, filter, fill, etc.). On undo, swaps the layer content back to the snapshot.
/// </summary>
public sealed class PixelChangeCommand : UndoCommand
{
    private readonly int layerIndex;
    private ImageFrame beforeSnapshot;
    private ImageFrame afterSnapshot;

    public PixelChangeCommand(string description, int layerIndex, ImageFrame beforeSnapshot, ImageFrame afterSnapshot)
        : base(description)
    {
        this.layerIndex = layerIndex;
        this.beforeSnapshot = beforeSnapshot;
        this.afterSnapshot = afterSnapshot;
    }

    public override void Undo(EditorDocument document) =>
        document.Layers[layerIndex].Content = beforeSnapshot.Clone();

    public override void Redo(EditorDocument document) =>
        document.Layers[layerIndex].Content = afterSnapshot.Clone();
}

/// <summary>
/// Records the addition of a layer. Undo removes it; redo adds it back.
/// </summary>
public sealed class AddLayerCommand : UndoCommand
{
    private readonly int insertIndex;
    private readonly Layer layerSnapshot;

    public AddLayerCommand(string description, int insertIndex, Layer layer)
        : base(description)
    {
        this.insertIndex = insertIndex;
        layerSnapshot = layer;
    }

    public override void Undo(EditorDocument document)
    {
        document.Layers.RemoveLayer(insertIndex);
        if (document.ActiveLayerIndex >= document.Layers.Count && document.Layers.Count > 0)
            document.ActiveLayerIndex = document.Layers.Count - 1;
    }

    public override void Redo(EditorDocument document)
    {
        document.Layers.InsertLayer(insertIndex, layerSnapshot);
        document.ActiveLayerIndex = insertIndex;
    }
}

/// <summary>
/// Records the removal of a layer. Undo re-inserts it; redo removes it again.
/// </summary>
public sealed class RemoveLayerCommand : UndoCommand
{
    private readonly int removedIndex;
    private readonly Layer layerSnapshot;
    private readonly int previousActiveIndex;

    public RemoveLayerCommand(string description, int removedIndex, Layer layer, int previousActiveIndex)
        : base(description)
    {
        this.removedIndex = removedIndex;
        layerSnapshot = layer;
        this.previousActiveIndex = previousActiveIndex;
    }

    public override void Undo(EditorDocument document)
    {
        document.Layers.InsertLayer(removedIndex, layerSnapshot);
        document.ActiveLayerIndex = previousActiveIndex;
    }

    public override void Redo(EditorDocument document)
    {
        document.Layers.RemoveLayer(removedIndex);
        if (document.ActiveLayerIndex >= document.Layers.Count && document.Layers.Count > 0)
            document.ActiveLayerIndex = document.Layers.Count - 1;
    }
}

/// <summary>
/// Records a layer property change (opacity, blend mode, visibility, name).
/// Lightweight — stores only old/new values, not pixel data.
/// </summary>
public sealed class LayerPropertyCommand : UndoCommand
{
    private readonly int layerIndex;
    private readonly Action<Layer> applyOldValue;
    private readonly Action<Layer> applyNewValue;

    public LayerPropertyCommand(string description, int layerIndex,
        Action<Layer> applyOldValue, Action<Layer> applyNewValue)
        : base(description)
    {
        this.layerIndex = layerIndex;
        this.applyOldValue = applyOldValue;
        this.applyNewValue = applyNewValue;
    }

    public override void Undo(EditorDocument document) =>
        applyOldValue(document.Layers[layerIndex]);

    public override void Redo(EditorDocument document) =>
        applyNewValue(document.Layers[layerIndex]);
}

/// <summary>
/// Records a layer reorder (move up/down). Undo reverses the move.
/// </summary>
public sealed class MoveLayerCommand : UndoCommand
{
    private readonly int fromIndex;
    private readonly int toIndex;

    public MoveLayerCommand(string description, int fromIndex, int toIndex)
        : base(description)
    {
        this.fromIndex = fromIndex;
        this.toIndex = toIndex;
    }

    public override void Undo(EditorDocument document)
    {
        document.Layers.MoveLayer(toIndex, fromIndex);
        document.ActiveLayerIndex = fromIndex;
    }

    public override void Redo(EditorDocument document)
    {
        document.Layers.MoveLayer(fromIndex, toIndex);
        document.ActiveLayerIndex = toIndex;
    }
}
