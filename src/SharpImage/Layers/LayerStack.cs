// SharpImage — Layer system for non-destructive editing.
// LayerStack, Layer, LayerMask, AdjustmentLayer, group clipping, flatten/merge.
// Bundle F of the feature roadmap.

using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Layers;

/// <summary>
/// A single layer in a layer stack. Contains an image, blend mode, opacity, mask, and position.
/// </summary>
public class Layer : IDisposable
{
    public string Name { get; set; }
    public ImageFrame Content { get; set; } = null!;
    public CompositeMode BlendMode { get; set; } = CompositeMode.Over;
    public double Opacity { get; set; } = 1.0;
    public bool Visible { get; set; } = true;
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }

    /// <summary>
    /// Optional grayscale mask controlling pixel visibility.
    /// White = fully visible, Black = fully transparent.
    /// </summary>
    public ImageFrame? Mask { get; set; }

    /// <summary>
    /// If set, this layer's content is clipped to the parent layer's alpha.
    /// The parent is the first non-clipped layer below this one in the stack.
    /// </summary>
    public bool ClippedToBelow { get; set; }

    public Layer(string name)
    {
        Name = name;
    }

    public void Dispose()
    {
        Content?.Dispose();
        Mask?.Dispose();
    }
}

/// <summary>
/// An adjustment layer that applies a non-destructive color/tone operation.
/// Instead of containing pixel data, it holds a transform function.
/// </summary>
public class AdjustmentLayer : Layer
{
    /// <summary>
    /// The adjustment function applied to the composited result below this layer.
    /// </summary>
    public Func<ImageFrame, ImageFrame> Adjustment { get; set; } = null!;

    public AdjustmentLayer(string name, Func<ImageFrame, ImageFrame> adjustment)
        : base(name)
    {
        Adjustment = adjustment;
    }
}

/// <summary>
/// An ordered stack of layers with compositing operations.
/// Layers are ordered bottom-to-top (index 0 is the bottom/background layer).
/// </summary>
public class LayerStack : IDisposable
{
    private readonly List<Layer> layers = new();

    public int Count => layers.Count;
    public uint CanvasWidth { get; }
    public uint CanvasHeight { get; }

    public LayerStack(uint canvasWidth, uint canvasHeight)
    {
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
    }

    /// <summary>
    /// Gets a layer by index (0 = bottom).
    /// </summary>
    public Layer this[int index] => layers[index];

    /// <summary>
    /// Adds a layer to the top of the stack.
    /// </summary>
    public void AddLayer(Layer layer)
    {
        layers.Add(layer);
    }

    /// <summary>
    /// Inserts a layer at the specified index.
    /// </summary>
    public void InsertLayer(int index, Layer layer)
    {
        layers.Insert(index, layer);
    }

    /// <summary>
    /// Removes a layer at the specified index and returns it.
    /// </summary>
    public Layer RemoveLayer(int index)
    {
        var layer = layers[index];
        layers.RemoveAt(index);
        return layer;
    }

    /// <summary>
    /// Moves a layer from one index to another.
    /// </summary>
    public void MoveLayer(int fromIndex, int toIndex)
    {
        var layer = layers[fromIndex];
        layers.RemoveAt(fromIndex);
        layers.Insert(toIndex, layer);
    }

    /// <summary>
    /// Flattens all visible layers into a single ImageFrame.
    /// Composites from bottom to top, respecting blend modes, opacity, masks, and clipping groups.
    /// </summary>
    public ImageFrame Flatten()
    {
        var canvas = new ImageFrame();
        canvas.Initialize(CanvasWidth, CanvasHeight, ColorspaceType.RGB, true);

        // Initialize to transparent
        for (int y = 0; y < (int)CanvasHeight; y++)
        {
            var row = canvas.GetPixelRowForWrite(y);
            row.Clear();
        }

        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (!layer.Visible) continue;

            if (layer is AdjustmentLayer adj)
            {
                // Apply adjustment to the current canvas
                var adjusted = adj.Adjustment(canvas);
                // Apply with mask if present
                if (adj.Mask != null)
                    ApplyWithMask(canvas, adjusted, adj.Mask, adj.Opacity, adj.OffsetX, adj.OffsetY);
                else if (adj.Opacity < 1.0)
                    BlendOpacity(canvas, adjusted, adj.Opacity);
                else
                {
                    // Replace canvas
                    canvas.Dispose();
                    canvas = adjusted;
                    continue;
                }
                adjusted.Dispose();
            }
            else
            {
                if (layer.Content == null) continue;

                // Prepare the layer content with opacity and mask applied
                var layerContent = PrepareLayerContent(layer);

                // Handle clipping groups
                if (layer.ClippedToBelow && i > 0)
                    ClipToLayerBelow(layerContent, canvas);

                // Composite onto canvas
                Composite.Apply(canvas, layerContent, layer.OffsetX, layer.OffsetY, layer.BlendMode);
                layerContent.Dispose();
            }
        }

        return canvas;
    }

    /// <summary>
    /// Merges a range of layers into a single layer.
    /// The merged layer replaces the range in the stack.
    /// </summary>
    public void MergeLayers(int startIndex, int count)
    {
        if (startIndex < 0 || startIndex + count > layers.Count)
            throw new ArgumentOutOfRangeException(nameof(startIndex));

        // Create a temporary stack with just these layers
        var tempStack = new LayerStack(CanvasWidth, CanvasHeight);
        for (int i = startIndex; i < startIndex + count; i++)
            tempStack.AddLayer(layers[i]);

        var merged = tempStack.Flatten();

        var mergedLayer = new Layer($"Merged ({layers[startIndex].Name}..{layers[startIndex + count - 1].Name})")
        {
            Content = merged,
            BlendMode = CompositeMode.Over,
            Opacity = 1.0,
            Visible = true
        };

        // Remove old layers (don't dispose — they may be referenced elsewhere)
        for (int i = 0; i < count; i++)
            layers.RemoveAt(startIndex);

        layers.Insert(startIndex, mergedLayer);
    }

    /// <summary>
    /// Merges all visible layers and returns the result without modifying the stack.
    /// </summary>
    public ImageFrame MergeVisible()
    {
        return Flatten();
    }

    public void Dispose()
    {
        foreach (var layer in layers)
            layer.Dispose();
        layers.Clear();
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private ImageFrame PrepareLayerContent(Layer layer)
    {
        int layerW = (int)layer.Content.Columns;
        int layerH = (int)layer.Content.Rows;
        int channels = layer.Content.NumberOfChannels;

        // If no mask or opacity modification needed, just clone
        if (layer.Mask == null && layer.Opacity >= 1.0)
            return CloneFrame(layer.Content);

        var prepared = new ImageFrame();
        prepared.Initialize(layerW, layerH, ColorspaceType.RGB, true);
        int dstChannels = prepared.NumberOfChannels;

        for (int y = 0; y < layerH; y++)
        {
            var srcRow = layer.Content.GetPixelRow(y);
            var dstRow = prepared.GetPixelRowForWrite(y);

            for (int x = 0; x < layerW; x++)
            {
                int srcOff = x * channels;
                int dstOff = x * dstChannels;

                // Copy RGB
                if (channels >= 3)
                {
                    dstRow[dstOff] = srcRow[srcOff];
                    dstRow[dstOff + 1] = srcRow[srcOff + 1];
                    dstRow[dstOff + 2] = srcRow[srcOff + 2];
                }
                else
                {
                    dstRow[dstOff] = srcRow[srcOff];
                    dstRow[dstOff + 1] = srcRow[srcOff];
                    dstRow[dstOff + 2] = srcRow[srcOff];
                }

                // Alpha
                double alpha = layer.Content.HasAlpha
                    ? srcRow[srcOff + channels - 1] * Quantum.Scale
                    : 1.0;

                alpha *= layer.Opacity;

                if (layer.Mask != null && y < (int)layer.Mask.Rows && x < (int)layer.Mask.Columns)
                {
                    int maskCh = layer.Mask.NumberOfChannels;
                    var maskRow = layer.Mask.GetPixelRow(y);
                    alpha *= maskRow[x * maskCh] * Quantum.Scale;
                }

                dstRow[dstOff + 3] = Quantum.Clamp((int)(alpha * Quantum.MaxValue));
            }
        }

        return prepared;
    }

    private static void ApplyWithMask(ImageFrame canvas, ImageFrame adjusted,
        ImageFrame mask, double opacity, int offsetX, int offsetY)
    {
        int width = (int)canvas.Columns;
        int height = (int)canvas.Rows;
        int channels = canvas.NumberOfChannels;
        int maskCh = mask.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var canRow = canvas.GetPixelRowForWrite(y);
            var adjRow = adjusted.GetPixelRow(y);

            int maskY = y - offsetY;
            bool hasMask = maskY >= 0 && maskY < (int)mask.Rows;

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                int maskX = x - offsetX;
                double maskAlpha = 0;

                if (hasMask && maskX >= 0 && maskX < (int)mask.Columns)
                {
                    var maskRow = mask.GetPixelRow(maskY);
                    maskAlpha = maskRow[maskX * maskCh] * Quantum.Scale;
                }

                double blend = maskAlpha * opacity;
                for (int c = 0; c < channels; c++)
                    canRow[off + c] = Quantum.Clamp((int)(canRow[off + c] * (1 - blend) + adjRow[off + c] * blend));
            }
        }
    }

    private static void BlendOpacity(ImageFrame canvas, ImageFrame adjusted, double opacity)
    {
        int width = (int)canvas.Columns;
        int height = (int)canvas.Rows;
        int channels = canvas.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var canRow = canvas.GetPixelRowForWrite(y);
            var adjRow = adjusted.GetPixelRow(y);

            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                for (int c = 0; c < channels; c++)
                    canRow[off + c] = Quantum.Clamp((int)(canRow[off + c] * (1 - opacity) + adjRow[off + c] * opacity));
            }
        }
    }

    private static void ClipToLayerBelow(ImageFrame layerContent, ImageFrame canvasBelow)
    {
        int width = (int)layerContent.Columns;
        int height = (int)layerContent.Rows;
        int channels = layerContent.NumberOfChannels;
        int canvasChannels = canvasBelow.NumberOfChannels;

        if (!layerContent.HasAlpha) return;

        for (int y = 0; y < height && y < (int)canvasBelow.Rows; y++)
        {
            var layerRow = layerContent.GetPixelRowForWrite(y);
            var canvasRow = canvasBelow.GetPixelRow(y);

            for (int x = 0; x < width && x < (int)canvasBelow.Columns; x++)
            {
                double canvasAlpha = canvasBelow.HasAlpha
                    ? canvasRow[x * canvasChannels + canvasChannels - 1] * Quantum.Scale
                    : 1.0;

                int alphaIdx = x * channels + channels - 1;
                double layerAlpha = layerRow[alphaIdx] * Quantum.Scale;
                layerRow[alphaIdx] = Quantum.Clamp((int)(layerAlpha * canvasAlpha * Quantum.MaxValue));
            }
        }
    }

    private static ImageFrame CloneFrame(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        var clone = new ImageFrame();
        clone.Initialize(width, height, source.Colorspace, source.HasAlpha);
        for (int y = 0; y < height; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = clone.GetPixelRowForWrite(y);
            srcRow.CopyTo(dstRow);
        }
        return clone;
    }
}
