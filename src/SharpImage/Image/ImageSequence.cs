using SharpImage.Core;

namespace SharpImage.Image;

/// <summary>
/// A container for multiple image frames forming an animation or multi-page image.
/// Supports GIF animation, APNG, multi-page TIFF, etc.
/// </summary>
public sealed class ImageSequence : IDisposable
{
    private readonly List<ImageFrame> frames = [];
    private bool disposed;

    /// <summary>
    /// The frames in this sequence.
    /// </summary>
    public IReadOnlyList<ImageFrame> Frames => frames;

    /// <summary>
    /// Number of frames.
    /// </summary>
    public int Count => frames.Count;

    /// <summary>
    /// Canvas width for the entire animation.
    /// </summary>
    public int CanvasWidth { get; set; }

    /// <summary>
    /// Canvas height for the entire animation.
    /// </summary>
    public int CanvasHeight { get; set; }

    /// <summary>
    /// Number of times the animation loops. 0 = infinite.
    /// </summary>
    public int LoopCount { get; set; }

    /// <summary>
    /// Background color index (GIF-specific).
    /// </summary>
    public int BackgroundColorIndex { get; set; }

    /// <summary>
    /// Source format name (e.g., "GIF", "APNG").
    /// </summary>
    public string FormatName { get; set; } = string.Empty;

    /// <summary>
    /// Access a frame by index.
    /// </summary>
    public ImageFrame this[int index] => frames[index];

    /// <summary>
    /// Add a frame to the sequence.
    /// </summary>
    public void AddFrame(ImageFrame frame)
    {
        frame.Scene = frames.Count;
        frames.Add(frame);

        // Auto-set canvas size from first frame if not set
        if (frames.Count == 1 && CanvasWidth == 0)
        {
            CanvasWidth = (int)frame.Columns;
            CanvasHeight = (int)frame.Rows;
        }
    }

    /// <summary>
    /// Remove a frame at the given index.
    /// </summary>
    public void RemoveFrame(int index)
    {
        var frame = frames[index];
        frames.RemoveAt(index);
        frame.Dispose();

        // Renumber scenes
        for (int i = index; i < frames.Count; i++)
            frames[i].Scene = i;
    }

    /// <summary>
    /// Total animation duration in centiseconds.
    /// </summary>
    public int TotalDuration => frames.Sum(f => f.Delay);

    /// <summary>
    /// Get the first frame (convenience for single-frame operations).
    /// </summary>
    public ImageFrame FirstFrame => frames.Count > 0
        ? frames[0]
        : throw new InvalidOperationException("Sequence has no frames.");

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var frame in frames)
            frame.Dispose();
        frames.Clear();
    }
}
