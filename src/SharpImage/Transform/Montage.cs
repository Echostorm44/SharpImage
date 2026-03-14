using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Transform;

/// <summary>
/// Montage, append, and coalesce operations for combining multiple images.
/// </summary>
public static class Montage
{
    /// <summary>
    /// Appends images either horizontally (left-to-right) or vertically (top-to-bottom).
    /// When horizontal, images are aligned by their top edge, total width is the sum of all widths,
    /// and total height is the maximum height.
    /// When vertical, images are aligned by their left edge.
    /// </summary>
    public static ImageFrame Append(ReadOnlySpan<ImageFrame> images, bool horizontal = true)
    {
        if (images.Length == 0)
            throw new ArgumentException("At least one image is required for append.");

        if (images.Length == 1)
            return CloneFrame(images[0]);

        bool hasAlpha = false;
        for (int i = 0; i < images.Length; i++)
        {
            if (images[i].HasAlpha)
            {
                hasAlpha = true;
                break;
            }
        }

        int totalWidth, totalHeight;
        if (horizontal)
        {
            totalWidth = 0;
            totalHeight = 0;
            for (int i = 0; i < images.Length; i++)
            {
                totalWidth += (int)images[i].Columns;
                totalHeight = Math.Max(totalHeight, (int)images[i].Rows);
            }
        }
        else
        {
            totalWidth = 0;
            totalHeight = 0;
            for (int i = 0; i < images.Length; i++)
            {
                totalWidth = Math.Max(totalWidth, (int)images[i].Columns);
                totalHeight += (int)images[i].Rows;
            }
        }

        var result = new ImageFrame();
        result.Initialize(totalWidth, totalHeight, ColorspaceType.SRGB, hasAlpha);
        int resultChannels = result.NumberOfChannels;

        // Fill with transparent black if alpha, otherwise black
        for (int y = 0; y < totalHeight; y++)
        {
            var row = result.GetPixelRowForWrite(y);
            row.Clear();
        }

        if (horizontal)
        {
            int xOffset = 0;
            for (int i = 0; i < images.Length; i++)
            {
                CopyImageTo(images[i], result, xOffset, 0);
                xOffset += (int)images[i].Columns;
            }
        }
        else
        {
            int yOffset = 0;
            for (int i = 0; i < images.Length; i++)
            {
                CopyImageTo(images[i], result, 0, yOffset);
                yOffset += (int)images[i].Rows;
            }
        }

        return result;
    }

    /// <summary>
    /// Tiles multiple images in a grid layout with optional spacing between tiles.
    /// Images are arranged left-to-right, top-to-bottom. If columns is 0, it auto-calculates
    /// a roughly square grid.
    /// </summary>
    public static ImageFrame Tile(ReadOnlySpan<ImageFrame> images, int columns = 0,
        int spacing = 0, ushort backgroundColor = 0)
    {
        if (images.Length == 0)
            throw new ArgumentException("At least one image is required for montage.");

        if (columns <= 0)
            columns = (int)Math.Ceiling(Math.Sqrt(images.Length));

        int rows = (images.Length + columns - 1) / columns;

        // Find maximum tile dimensions
        int maxTileWidth = 0;
        int maxTileHeight = 0;
        bool hasAlpha = false;
        for (int i = 0; i < images.Length; i++)
        {
            maxTileWidth = Math.Max(maxTileWidth, (int)images[i].Columns);
            maxTileHeight = Math.Max(maxTileHeight, (int)images[i].Rows);
            if (images[i].HasAlpha) hasAlpha = true;
        }

        int totalWidth = columns * maxTileWidth + (columns - 1) * spacing;
        int totalHeight = rows * maxTileHeight + (rows - 1) * spacing;

        var result = new ImageFrame();
        result.Initialize(totalWidth, totalHeight, ColorspaceType.SRGB, hasAlpha);
        int resultChannels = result.NumberOfChannels;

        // Fill background
        for (int y = 0; y < totalHeight; y++)
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = 0; x < totalWidth; x++)
            {
                int off = x * resultChannels;
                row[off] = backgroundColor;
                row[off + 1] = backgroundColor;
                row[off + 2] = backgroundColor;
                if (hasAlpha)
                    row[off + 3] = Quantum.MaxValue;
            }
        }

        // Place each image centered in its tile cell
        for (int i = 0; i < images.Length; i++)
        {
            int col = i % columns;
            int row = i / columns;

            int cellX = col * (maxTileWidth + spacing);
            int cellY = row * (maxTileHeight + spacing);

            // Center image within its cell
            int imgW = (int)images[i].Columns;
            int imgH = (int)images[i].Rows;
            int offsetX = cellX + (maxTileWidth - imgW) / 2;
            int offsetY = cellY + (maxTileHeight - imgH) / 2;

            CopyImageTo(images[i], result, offsetX, offsetY);
        }

        return result;
    }

    /// <summary>
    /// Coalesces an animation sequence by compositing each frame onto the canvas,
    /// producing a sequence of full-canvas frames suitable for editing. Handles
    /// disposal methods (None, Background, Previous).
    /// </summary>
    public static ImageSequence Coalesce(ImageSequence sequence)
    {
        if (sequence.Count == 0)
            throw new ArgumentException("Sequence has no frames to coalesce.");

        int canvasWidth = sequence.CanvasWidth;
        int canvasHeight = sequence.CanvasHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            canvasWidth = (int)sequence[0].Columns;
            canvasHeight = (int)sequence[0].Rows;
        }

        var result = new ImageSequence
        {
            CanvasWidth = canvasWidth,
            CanvasHeight = canvasHeight,
            LoopCount = sequence.LoopCount,
            FormatName = sequence.FormatName
        };

        // Working canvas — the accumulated state
        var canvas = new ImageFrame();
        canvas.Initialize(canvasWidth, canvasHeight, ColorspaceType.SRGB, true);
        ClearFrame(canvas);

        ImageFrame? previousCanvas = null;

        for (int i = 0; i < sequence.Count; i++)
        {
            var frame = sequence[i];
            int frameX = 0;
            int frameY = 0;

            // Use Page offset if available
            if (frame.Page.X != 0 || frame.Page.Y != 0)
            {
                frameX = frame.Page.X;
                frameY = frame.Page.Y;
            }

            // Composite frame onto canvas (alpha-aware — transparent pixels don't overwrite)
            CompositeOver(frame, canvas, frameX, frameY);

            // Snapshot the coalesced frame
            var coalesced = CloneFrame(canvas);
            coalesced.Delay = frame.Delay;
            coalesced.DisposeMethod = DisposeType.None;
            result.AddFrame(coalesced);

            // Handle disposal for next frame
            switch (frame.DisposeMethod)
            {
                case DisposeType.Background:
                    // Clear the frame region to transparent
                    ClearRegion(canvas, frameX, frameY, (int)frame.Columns, (int)frame.Rows);
                    break;

                case DisposeType.Previous:
                    // Restore canvas to state before this frame
                    if (previousCanvas != null)
                    {
                        CopyFrameData(previousCanvas, canvas);
                    }
                    break;

                case DisposeType.None:
                case DisposeType.Undefined:
                default:
                    // Keep canvas as-is (leave composited)
                    break;
            }

            // Save canvas state before disposal (for Previous disposal of future frames)
            if (frame.DisposeMethod != DisposeType.Previous)
            {
                previousCanvas?.Dispose();
                previousCanvas = CloneFrame(canvas);
            }
        }

        previousCanvas?.Dispose();
        canvas.Dispose();

        return result;
    }

    /// <summary>
    /// Copies source image pixels onto the destination at the given offset.
    /// Handles mismatched channel counts gracefully.
    /// </summary>
    private static void CopyImageTo(ImageFrame source, ImageFrame dest, int offsetX, int offsetY)
    {
        int srcW = (int)source.Columns;
        int srcH = (int)source.Rows;
        int dstW = (int)dest.Columns;
        int dstH = (int)dest.Rows;
        int srcCh = source.NumberOfChannels;
        int dstCh = dest.NumberOfChannels;

        int startY = Math.Max(0, -offsetY);
        int endY = Math.Min(srcH, dstH - offsetY);
        int startX = Math.Max(0, -offsetX);
        int endX = Math.Min(srcW, dstW - offsetX);

        if (startY >= endY || startX >= endX) return;

        for (int y = startY; y < endY; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = dest.GetPixelRowForWrite(y + offsetY);

            if (srcCh == dstCh)
            {
                int srcStart = startX * srcCh;
                int dstStart = (startX + offsetX) * dstCh;
                int len = (endX - startX) * srcCh;
                srcRow.Slice(srcStart, len).CopyTo(dstRow.Slice(dstStart, len));
            }
            else
            {
                for (int x = startX; x < endX; x++)
                {
                    int sOff = x * srcCh;
                    int dOff = (x + offsetX) * dstCh;

                    dstRow[dOff] = srcRow[sOff];
                    dstRow[dOff + 1] = srcCh > 1 ? srcRow[sOff + 1] : srcRow[sOff];
                    dstRow[dOff + 2] = srcCh > 2 ? srcRow[sOff + 2] : srcRow[sOff];
                    if (dstCh > 3)
                        dstRow[dOff + 3] = srcCh > 3 ? srcRow[sOff + 3] : Quantum.MaxValue;
                }
            }
        }
    }

    /// <summary>
    /// Clones a frame including all pixel data.
    /// </summary>
    private static ImageFrame CloneFrame(ImageFrame source)
    {
        var clone = new ImageFrame();
        clone.Initialize((int)source.Columns, (int)source.Rows, source.Colorspace, source.HasAlpha);
        CopyFrameData(source, clone);
        return clone;
    }

    /// <summary>
    /// Copies all pixel data from source to dest (must be same dimensions).
    /// </summary>
    private static void CopyFrameData(ImageFrame source, ImageFrame dest)
    {
        int channels = source.NumberOfChannels;
        for (int y = 0; y < (int)source.Rows; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = dest.GetPixelRowForWrite(y);
            srcRow.Slice(0, (int)source.Columns * channels).CopyTo(dstRow);
        }
    }

    /// <summary>
    /// Alpha-aware compositing (Porter-Duff Over). Source pixels with alpha=0
    /// leave the destination unchanged. Used by Coalesce for frame compositing.
    /// </summary>
    private static void CompositeOver(ImageFrame source, ImageFrame dest, int offsetX, int offsetY)
    {
        int srcW = (int)source.Columns;
        int srcH = (int)source.Rows;
        int dstW = (int)dest.Columns;
        int dstH = (int)dest.Rows;
        int srcCh = source.NumberOfChannels;
        int dstCh = dest.NumberOfChannels;

        int startY = Math.Max(0, -offsetY);
        int endY = Math.Min(srcH, dstH - offsetY);
        int startX = Math.Max(0, -offsetX);
        int endX = Math.Min(srcW, dstW - offsetX);

        if (startY >= endY || startX >= endX) return;

        bool srcHasAlpha = srcCh >= 4;
        bool dstHasAlpha = dstCh >= 4;

        for (int y = startY; y < endY; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = dest.GetPixelRowForWrite(y + offsetY);

            for (int x = startX; x < endX; x++)
            {
                int sOff = x * srcCh;
                int dOff = (x + offsetX) * dstCh;

                ushort srcAlpha = srcHasAlpha ? srcRow[sOff + 3] : Quantum.MaxValue;

                if (srcAlpha == 0)
                    continue; // Fully transparent — skip

                if (srcAlpha == Quantum.MaxValue)
                {
                    // Fully opaque — direct copy
                    dstRow[dOff] = srcRow[sOff];
                    dstRow[dOff + 1] = srcCh > 1 ? srcRow[sOff + 1] : srcRow[sOff];
                    dstRow[dOff + 2] = srcCh > 2 ? srcRow[sOff + 2] : srcRow[sOff];
                    if (dstHasAlpha)
                        dstRow[dOff + 3] = Quantum.MaxValue;
                }
                else
                {
                    // Semi-transparent — Porter-Duff Over
                    ushort dstAlpha = dstHasAlpha ? dstRow[dOff + 3] : Quantum.MaxValue;
                    int sA = srcAlpha;
                    int dA = dstAlpha;
                    int outA = sA + dA * (Quantum.MaxValue - sA) / Quantum.MaxValue;

                    if (outA == 0)
                        continue;

                    for (int c = 0; c < 3 && c < srcCh; c++)
                    {
                        int sC = srcRow[sOff + c];
                        int dC = dstRow[dOff + c];
                        dstRow[dOff + c] = (ushort)((sC * sA + dC * dA * (Quantum.MaxValue - sA) / Quantum.MaxValue) / outA);
                    }

                    if (dstHasAlpha)
                        dstRow[dOff + 3] = (ushort)outA;
                }
            }
        }
    }

    /// <summary>
    /// Fills an entire frame with transparent black.
    /// </summary>
    private static void ClearFrame(ImageFrame frame)
    {
        for (int y = 0; y < (int)frame.Rows; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            row.Clear();
        }
    }

    /// <summary>
    /// Clears a rectangular region to transparent black.
    /// </summary>
    private static void ClearRegion(ImageFrame frame, int x, int y, int width, int height)
    {
        int channels = frame.NumberOfChannels;
        int frameW = (int)frame.Columns;
        int frameH = (int)frame.Rows;

        int startX = Math.Max(0, x);
        int startY = Math.Max(0, y);
        int endX = Math.Min(frameW, x + width);
        int endY = Math.Min(frameH, y + height);

        for (int row = startY; row < endY; row++)
        {
            var rowSpan = frame.GetPixelRowForWrite(row);
            int off = startX * channels;
            int len = (endX - startX) * channels;
            rowSpan.Slice(off, len).Clear();
        }
    }
}
