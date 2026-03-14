// SharpImage — Content-Aware Resize via Seam Carving.
// Removes low-energy seams to resize images while preserving important content.
// Uses Sobel gradient energy, dynamic programming for optimal seam finding.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Transform;

/// <summary>
/// Content-aware image resizing using the seam carving algorithm.
/// Removes vertical or horizontal seams of minimum energy to reduce image dimensions
/// while preserving visually important content.
/// </summary>
public static class SeamCarving
{
    /// <summary>
    /// Content-aware resize to the target dimensions.
    /// Removes seams from the image to reduce width and/or height while preserving important content.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="targetWidth">Target width (must be less than or equal to source width).</param>
    /// <param name="targetHeight">Target height (must be less than or equal to source height).</param>
    /// <returns>New image resized to the target dimensions.</returns>
    public static ImageFrame Apply(ImageFrame source, int targetWidth, int targetHeight)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;

        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException("Target dimensions must be positive.");
        if (targetWidth > srcWidth)
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "Target width cannot exceed source width. Seam carving only reduces dimensions.");
        if (targetHeight > srcHeight)
            throw new ArgumentOutOfRangeException(nameof(targetHeight), "Target height cannot exceed source height. Seam carving only reduces dimensions.");

        // Work on a mutable copy
        var current = CloneFrame(source);

        // Remove vertical seams (reduce width)
        int verticalSeamsToRemove = srcWidth - targetWidth;
        for (int i = 0; i < verticalSeamsToRemove; i++)
        {
            int curWidth = (int)current.Columns;
            int curHeight = (int)current.Rows;
            var energyPool = ArrayPool<double>.Shared.Rent(curWidth * curHeight);
            try
            {
                ComputeEnergyMap(current, energyPool);
                var seamPath = FindVerticalSeam(energyPool, curWidth, curHeight);
                var reduced = RemoveVerticalSeam(current, seamPath);
                current.Dispose();
                current = reduced;
            }
            finally
            {
                ArrayPool<double>.Shared.Return(energyPool);
            }
        }

        // Remove horizontal seams (reduce height)
        int horizontalSeamsToRemove = srcHeight - targetHeight;
        for (int i = 0; i < horizontalSeamsToRemove; i++)
        {
            int curWidth = (int)current.Columns;
            int curHeight = (int)current.Rows;
            var energyPool = ArrayPool<double>.Shared.Rent(curWidth * curHeight);
            try
            {
                ComputeEnergyMap(current, energyPool);
                var seamPath = FindHorizontalSeam(energyPool, curWidth, curHeight);
                var reduced = RemoveHorizontalSeam(current, seamPath);
                current.Dispose();
                current = reduced;
            }
            finally
            {
                ArrayPool<double>.Shared.Return(energyPool);
            }
        }

        return current;
    }

    /// <summary>
    /// Computes an energy map for visualization, showing pixel importance.
    /// High-energy pixels appear bright (important content), low-energy pixels appear dark (removable).
    /// </summary>
    public static ImageFrame GetEnergyMap(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        var energy = ComputeEnergyMapAlloc(source);

        // Find max energy for normalization
        double maxEnergy = 0;
        for (int i = 0; i < energy.Length; i++)
        {
            if (energy[i] > maxEnergy) maxEnergy = energy[i];
        }

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.SRGB, false);
        double scale = maxEnergy > 0 ? Quantum.MaxValue / maxEnergy : 1.0;

        for (int y = 0; y < height; y++)
        {
            var outRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = Quantum.Clamp(energy[y * width + x] * scale);
                int offset = x * 3;
                outRow[offset] = val;
                outRow[offset + 1] = val;
                outRow[offset + 2] = val;
            }
        }

        return result;
    }

    // ─── Energy Computation ────────────────────────────────────────

    /// <summary>
    /// Computes energy map using dual-gradient (Sobel) energy function into a pre-allocated buffer.
    /// Energy = sqrt(Gx² + Gy²) where Gx,Gy are horizontal/vertical Sobel gradients.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeEnergyMap(ImageFrame image, double[] energy)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int colorChannels = hasAlpha ? channels - 1 : channels;

        Parallel.For(0, height, y =>
        {
            var rowAbove = image.GetPixelRow(Math.Max(0, y - 1));
            var rowCurrent = image.GetPixelRow(y);
            var rowBelow = image.GetPixelRow(Math.Min(height - 1, y + 1));

            for (int x = 0; x < width; x++)
            {
                int xLeft = Math.Max(0, x - 1);
                int xRight = Math.Min(width - 1, x + 1);

                double gx = 0, gy = 0;
                for (int c = 0; c < colorChannels; c++)
                {
                    double left = rowCurrent[xLeft * channels + c];
                    double right = rowCurrent[xRight * channels + c];
                    double dx = right - left;

                    double above = rowAbove[x * channels + c];
                    double below = rowBelow[x * channels + c];
                    double dy = below - above;

                    gx += dx * dx;
                    gy += dy * dy;
                }

                energy[y * width + x] = Math.Sqrt(gx + gy);
            }
        });
    }

    /// <summary>
    /// Computes energy map (allocating version for public API GetEnergyMap).
    /// </summary>
    private static double[] ComputeEnergyMapAlloc(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        var energy = new double[width * height];
        ComputeEnergyMap(image, energy);
        return energy;
    }

    // ─── Vertical Seam Finding ─────────────────────────────────────

    /// <summary>
    /// Finds the minimum-energy vertical seam using dynamic programming.
    /// Returns an array of x-coordinates, one per row.
    /// </summary>
    private static int[] FindVerticalSeam(double[] energy, int width, int height)
    {
        // Cumulative energy matrix
        var cumulative = ArrayPool<double>.Shared.Rent(width * height);
        try
        {
            // First row: copy energy directly
            Array.Copy(energy, 0, cumulative, 0, width);

            // Fill rows top-to-bottom
            for (int y = 1; y < height; y++)
            {
                int rowOffset = y * width;
                int prevRowOffset = (y - 1) * width;

                for (int x = 0; x < width; x++)
                {
                    double minParent = cumulative[prevRowOffset + x];

                    if (x > 0)
                        minParent = Math.Min(minParent, cumulative[prevRowOffset + x - 1]);
                    if (x < width - 1)
                        minParent = Math.Min(minParent, cumulative[prevRowOffset + x + 1]);

                    cumulative[rowOffset + x] = energy[rowOffset + x] + minParent;
                }
            }

            // Find minimum in last row
            int lastRowOffset = (height - 1) * width;
            int minX = 0;
            double minVal = cumulative[lastRowOffset];
            for (int x = 1; x < width; x++)
            {
                if (cumulative[lastRowOffset + x] < minVal)
                {
                    minVal = cumulative[lastRowOffset + x];
                    minX = x;
                }
            }

            // Backtrack to find seam path
            var seam = new int[height];
            seam[height - 1] = minX;

            for (int y = height - 2; y >= 0; y--)
            {
                int prevX = seam[y + 1];
                int rowOff = y * width;

                int bestX = prevX;
                double bestVal = cumulative[rowOff + prevX];

                if (prevX > 0 && cumulative[rowOff + prevX - 1] < bestVal)
                {
                    bestVal = cumulative[rowOff + prevX - 1];
                    bestX = prevX - 1;
                }
                if (prevX < width - 1 && cumulative[rowOff + prevX + 1] < bestVal)
                {
                    bestX = prevX + 1;
                }

                seam[y] = bestX;
            }

            return seam;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(cumulative);
        }
    }

    // ─── Horizontal Seam Finding ───────────────────────────────────

    /// <summary>
    /// Finds the minimum-energy horizontal seam using dynamic programming.
    /// Returns an array of y-coordinates, one per column.
    /// </summary>
    private static int[] FindHorizontalSeam(double[] energy, int width, int height)
    {
        var cumulative = ArrayPool<double>.Shared.Rent(width * height);
        try
        {
            // First column: copy energy directly
            for (int y = 0; y < height; y++)
                cumulative[y * width] = energy[y * width];

            // Fill columns left-to-right
            for (int x = 1; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    double minParent = cumulative[y * width + x - 1];

                    if (y > 0)
                        minParent = Math.Min(minParent, cumulative[(y - 1) * width + x - 1]);
                    if (y < height - 1)
                        minParent = Math.Min(minParent, cumulative[(y + 1) * width + x - 1]);

                    cumulative[y * width + x] = energy[y * width + x] + minParent;
                }
            }

            // Find minimum in last column
            int minY = 0;
            double minVal = cumulative[0 * width + width - 1];
            for (int y = 1; y < height; y++)
            {
                double val = cumulative[y * width + width - 1];
                if (val < minVal)
                {
                    minVal = val;
                    minY = y;
                }
            }

            // Backtrack
            var seam = new int[width];
            seam[width - 1] = minY;

            for (int x = width - 2; x >= 0; x--)
            {
                int prevY = seam[x + 1];
                int bestY = prevY;
                double bestVal = cumulative[prevY * width + x];

                if (prevY > 0 && cumulative[(prevY - 1) * width + x] < bestVal)
                {
                    bestVal = cumulative[(prevY - 1) * width + x];
                    bestY = prevY - 1;
                }
                if (prevY < height - 1 && cumulative[(prevY + 1) * width + x] < bestVal)
                {
                    bestY = prevY + 1;
                }

                seam[x] = bestY;
            }

            return seam;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(cumulative);
        }
    }

    // ─── Seam Removal ──────────────────────────────────────────────

    /// <summary>
    /// Removes one vertical seam from the image, reducing width by 1.
    /// </summary>
    private static ImageFrame RemoveVerticalSeam(ImageFrame source, int[] seamX)
    {
        int oldWidth = (int)source.Columns;
        int height = (int)source.Rows;
        int newWidth = oldWidth - 1;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(newWidth, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            int removeX = seamX[y];

            // Copy pixels before the seam
            int leftPixels = removeX * channels;
            if (leftPixels > 0)
                srcRow[..leftPixels].CopyTo(dstRow);

            // Copy pixels after the seam
            int rightSrcStart = (removeX + 1) * channels;
            int rightDstStart = removeX * channels;
            int rightLen = (oldWidth - removeX - 1) * channels;
            if (rightLen > 0)
                srcRow.Slice(rightSrcStart, rightLen).CopyTo(dstRow[rightDstStart..]);
        });

        return result;
    }

    /// <summary>
    /// Removes one horizontal seam from the image, reducing height by 1.
    /// </summary>
    private static ImageFrame RemoveHorizontalSeam(ImageFrame source, int[] seamY)
    {
        int width = (int)source.Columns;
        int oldHeight = (int)source.Rows;
        int newHeight = oldHeight - 1;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, newHeight, source.Colorspace, source.HasAlpha);

        for (int x = 0; x < width; x++)
        {
            int removeY = seamY[x];
            int dstY = 0;

            for (int y = 0; y < oldHeight; y++)
            {
                if (y == removeY) continue;

                var srcRow = source.GetPixelRow(y);
                var dstRow = result.GetPixelRowForWrite(dstY);
                int srcOffset = x * channels;
                int dstOffset = x * channels;

                for (int c = 0; c < channels; c++)
                    dstRow[dstOffset + c] = srcRow[srcOffset + c];

                dstY++;
            }
        }

        return result;
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private static ImageFrame CloneFrame(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var clone = new ImageFrame();
        clone.Initialize(width, height, source.Colorspace, source.HasAlpha);

        for (int y = 0; y < height; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = clone.GetPixelRowForWrite(y);
            srcRow[..(width * channels)].CopyTo(dstRow);
        }

        return clone;
    }
}
