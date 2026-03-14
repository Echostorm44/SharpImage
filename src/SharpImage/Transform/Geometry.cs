using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Transform;

/// <summary>
/// Crop, flip, flop, and rotation operations. All operations return new ImageFrame instances.
/// </summary>
public static class Geometry
{
    /// <summary>
    /// Crops a rectangular region from the source image.
    /// </summary>
    public static ImageFrame Crop(ImageFrame source, int x, int y, int width, int height)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;

        // Clamp to valid bounds
        if (x < 0)
        {
            width += x;
            x = 0;
        }
        if (y < 0)
        {
            height += y;
            y = 0;
        }
        width = Math.Min(width, srcWidth - x);
        height = Math.Min(height, srcHeight - y);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Crop region is empty or outside image bounds.");
        }

        int channels = source.NumberOfChannels;
        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        for (int row = 0;row < height;row++)
        {
            var srcRow = source.GetPixelRow(y + row);
            var dstRow = result.GetPixelRowForWrite(row);

            int srcStart = x * channels;
            int copyLen = width * channels;
            srcRow.Slice(srcStart, copyLen).CopyTo(dstRow);
        }

        return result;
    }

    /// <summary>
    /// Flips the image vertically (top ↔ bottom mirror).
    /// </summary>
    public static ImageFrame Flip(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        for (int y = 0;y < height;y++)
        {
            var srcRow = source.GetPixelRow(height - 1 - y);
            var dstRow = result.GetPixelRowForWrite(y);
            srcRow.Slice(0, width * channels).CopyTo(dstRow);
        }

        return result;
    }

    /// <summary>
    /// Flops the image horizontally (left ↔ right mirror).
    /// </summary>
    public static ImageFrame Flop(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        for (int y = 0;y < height;y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0;x < width;x++)
            {
                int srcOffset = (width - 1 - x) * channels;
                int dstOffset = x * channels;
                for (int c = 0;c < channels;c++)
                {
                    dstRow[dstOffset + c] = srcRow[srcOffset + c];
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Rotates the image by a multiple of 90 degrees (90, 180, 270). For arbitrary angles, use RotateArbitrary.
    /// </summary>
    public static ImageFrame Rotate(ImageFrame source, RotationAngle angle)
    {
        return angle switch
        {
            RotationAngle.Rotate90 => Rotate90(source),
            RotationAngle.Rotate180 => Rotate180(source),
            RotationAngle.Rotate270 => Rotate270(source),
            _ => throw new ArgumentOutOfRangeException(nameof(angle))
        };
    }

    private static ImageFrame Rotate90(ImageFrame source)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // 90° CW: new dimensions are (height, width)
        var result = new ImageFrame();
        result.Initialize(srcHeight, srcWidth, source.Colorspace, source.HasAlpha);

        for (int y = 0;y < srcHeight;y++)
        {
            var srcRow = source.GetPixelRow(y);
            for (int x = 0;x < srcWidth;x++)
            {
                // Source(x, y) → Dest(srcHeight - 1 - y, x)
                int dstX = srcHeight - 1 - y;
                int dstY = x;
                var dstRow = result.GetPixelRowForWrite(dstY);

                int srcOffset = x * channels;
                int dstOffset = dstX * channels;
                for (int c = 0;c < channels;c++)
                {
                    dstRow[dstOffset + c] = srcRow[srcOffset + c];
                }
            }
        }

        return result;
    }

    private static ImageFrame Rotate180(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        for (int y = 0;y < height;y++)
        {
            var srcRow = source.GetPixelRow(height - 1 - y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0;x < width;x++)
            {
                int srcOffset = (width - 1 - x) * channels;
                int dstOffset = x * channels;
                for (int c = 0;c < channels;c++)
                {
                    dstRow[dstOffset + c] = srcRow[srcOffset + c];
                }
            }
        }

        return result;
    }

    private static ImageFrame Rotate270(ImageFrame source)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // 270° CW (= 90° CCW): new dimensions are (height, width)
        var result = new ImageFrame();
        result.Initialize(srcHeight, srcWidth, source.Colorspace, source.HasAlpha);

        for (int y = 0;y < srcHeight;y++)
        {
            var srcRow = source.GetPixelRow(y);
            for (int x = 0;x < srcWidth;x++)
            {
                // Source(x, y) → Dest(y, srcWidth - 1 - x)
                int dstX = y;
                int dstY = srcWidth - 1 - x;
                var dstRow = result.GetPixelRowForWrite(dstY);

                int srcOffset = x * channels;
                int dstOffset = dstX * channels;
                for (int c = 0;c < channels;c++)
                {
                    dstRow[dstOffset + c] = srcRow[srcOffset + c];
                }
            }
        }

        return result;
    }

    // ─── Trim ─────────────────────────────────────────────────────

    /// <summary>
    /// Trims uniform borders from the image. Detects the border color from corner pixels
    /// and removes matching edges within the specified fuzz tolerance.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="fuzz">Color distance tolerance (0.0 = exact match, 1.0 = match everything).
    /// Compared against normalized Euclidean distance in [0,1].</param>
    public static ImageFrame Trim(ImageFrame source, double fuzz = 0.0)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        double fuzzSquared = fuzz * fuzz * Quantum.MaxValue * (double)Quantum.MaxValue * channels;

        // Sample border color from top-left corner
        var cornerRow = source.GetPixelRow(0);
        ushort[] borderColor = new ushort[channels];
        for (int c = 0; c < channels; c++)
            borderColor[c] = cornerRow[c];

        // Scan inward from each edge to find the content bounding box
        int top = 0, bottom = height - 1, left = 0, right = width - 1;

        // Top edge
        for (int y = 0; y < height; y++)
        {
            if (!IsRowBorder(source, y, borderColor, fuzzSquared)) break;
            top = y + 1;
        }

        // Bottom edge
        for (int y = height - 1; y >= top; y--)
        {
            if (!IsRowBorder(source, y, borderColor, fuzzSquared)) break;
            bottom = y - 1;
        }

        // Left edge
        for (int x = 0; x < width; x++)
        {
            if (!IsColumnBorder(source, x, top, bottom, borderColor, fuzzSquared)) break;
            left = x + 1;
        }

        // Right edge
        for (int x = width - 1; x >= left; x--)
        {
            if (!IsColumnBorder(source, x, top, bottom, borderColor, fuzzSquared)) break;
            right = x - 1;
        }

        int trimWidth = right - left + 1;
        int trimHeight = bottom - top + 1;

        if (trimWidth <= 0 || trimHeight <= 0)
        {
            // Entire image is border — return 1×1 transparent pixel
            var empty = new ImageFrame();
            empty.Initialize(1, 1, source.Colorspace, true);
            return empty;
        }

        return Crop(source, left, top, trimWidth, trimHeight);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPixelMatch(ReadOnlySpan<ushort> pixel, ushort[] borderColor, int channels, double fuzzSquared)
    {
        double distSq = 0;
        for (int c = 0; c < channels; c++)
        {
            double diff = pixel[c] - borderColor[c];
            distSq += diff * diff;
        }
        return distSq <= fuzzSquared;
    }

    private static bool IsRowBorder(ImageFrame source, int y, ushort[] borderColor, double fuzzSquared)
    {
        var row = source.GetPixelRow(y);
        int width = (int)source.Columns;
        int channels = source.NumberOfChannels;

        for (int x = 0; x < width; x++)
        {
            if (!IsPixelMatch(row.Slice(x * channels, channels), borderColor, channels, fuzzSquared))
                return false;
        }
        return true;
    }

    private static bool IsColumnBorder(ImageFrame source, int x, int top, int bottom,
        ushort[] borderColor, double fuzzSquared)
    {
        int channels = source.NumberOfChannels;
        for (int y = top; y <= bottom; y++)
        {
            var row = source.GetPixelRow(y);
            if (!IsPixelMatch(row.Slice(x * channels, channels), borderColor, channels, fuzzSquared))
                return false;
        }
        return true;
    }

    // ─── Extent ──────────────────────────────────────────────────

    /// <summary>
    /// Extends the image canvas to new dimensions, placing the original at the specified offset.
    /// New areas are filled with the background color (default black/transparent).
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="newWidth">New canvas width.</param>
    /// <param name="newHeight">New canvas height.</param>
    /// <param name="xOffset">X position of original image on new canvas.</param>
    /// <param name="yOffset">Y position of original image on new canvas.</param>
    /// <param name="backgroundR">Background red channel value.</param>
    /// <param name="backgroundG">Background green channel value.</param>
    /// <param name="backgroundB">Background blue channel value.</param>
    public static ImageFrame Extent(ImageFrame source, int newWidth, int newHeight,
        int xOffset = 0, int yOffset = 0,
        ushort backgroundR = 0, ushort backgroundG = 0, ushort backgroundB = 0)
    {
        if (newWidth <= 0 || newHeight <= 0)
            throw new ArgumentException("Extent dimensions must be positive.");

        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(newWidth, newHeight, source.Colorspace, source.HasAlpha);

        // Fill background — always fill so RGBA images get opaque alpha on new areas
        for (int y = 0; y < newHeight; y++)
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = 0; x < newWidth; x++)
            {
                int idx = x * channels;
                row[idx] = backgroundR;
                row[idx + 1] = backgroundG;
                row[idx + 2] = backgroundB;
                if (channels == 4)
                    row[idx + 3] = Quantum.MaxValue;
            }
        }

        // Composite original at offset
        int copyStartY = Math.Max(0, yOffset);
        int copyEndY = Math.Min(newHeight, yOffset + srcHeight);
        int copyStartX = Math.Max(0, xOffset);
        int copyEndX = Math.Min(newWidth, xOffset + srcWidth);

        for (int y = copyStartY; y < copyEndY; y++)
        {
            int srcY = y - yOffset;
            if (srcY < 0 || srcY >= srcHeight) continue;

            var srcRow = source.GetPixelRow(srcY);
            var dstRow = result.GetPixelRowForWrite(y);

            int srcStartX = copyStartX - xOffset;
            int copyWidth = copyEndX - copyStartX;
            if (srcStartX < 0 || copyWidth <= 0) continue;

            srcRow.Slice(srcStartX * channels, copyWidth * channels)
                  .CopyTo(dstRow.Slice(copyStartX * channels));
        }

        return result;
    }

    // ─── Shave ───────────────────────────────────────────────────

    /// <summary>
    /// Removes equal pixel strips from all four edges of the image.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="horizontal">Pixels to remove from left and right edges.</param>
    /// <param name="vertical">Pixels to remove from top and bottom edges.</param>
    public static ImageFrame Shave(ImageFrame source, int horizontal, int vertical)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;

        int newWidth = width - 2 * horizontal;
        int newHeight = height - 2 * vertical;

        if (newWidth <= 0 || newHeight <= 0)
            throw new ArgumentException("Shave amount exceeds image dimensions.");

        return Crop(source, horizontal, vertical, newWidth, newHeight);
    }

    // ─── Chop ────────────────────────────────────────────────────

    /// <summary>
    /// Removes a rectangular region from the image and collapses the remaining pixels.
    /// Removes a horizontal strip (if width spans the image) or vertical strip, then
    /// shifts surrounding pixels to fill the gap.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="chopX">X start of region to remove.</param>
    /// <param name="chopY">Y start of region to remove.</param>
    /// <param name="chopWidth">Width of region to remove.</param>
    /// <param name="chopHeight">Height of region to remove.</param>
    public static ImageFrame Chop(ImageFrame source, int chopX, int chopY, int chopWidth, int chopHeight)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Clamp to image bounds
        chopX = Math.Clamp(chopX, 0, srcWidth);
        chopY = Math.Clamp(chopY, 0, srcHeight);
        chopWidth = Math.Min(chopWidth, srcWidth - chopX);
        chopHeight = Math.Min(chopHeight, srcHeight - chopY);

        int newWidth = srcWidth - chopWidth;
        int newHeight = srcHeight - chopHeight;
        if (newWidth <= 0 || newHeight <= 0)
            throw new ArgumentException("Chop region covers entire image.");

        var result = new ImageFrame();
        result.Initialize(newWidth, newHeight, source.Colorspace, source.HasAlpha);

        int dstY = 0;
        for (int srcY = 0; srcY < srcHeight; srcY++)
        {
            // Skip rows in the chopped vertical range
            if (srcY >= chopY && srcY < chopY + chopHeight)
                continue;

            var srcRow = source.GetPixelRow(srcY);
            var dstRow = result.GetPixelRowForWrite(dstY);

            // Copy pixels before the chop region
            if (chopX > 0)
            {
                srcRow.Slice(0, chopX * channels)
                      .CopyTo(dstRow.Slice(0, chopX * channels));
            }

            // Copy pixels after the chop region
            int afterChopX = chopX + chopWidth;
            int afterWidth = srcWidth - afterChopX;
            if (afterWidth > 0)
            {
                srcRow.Slice(afterChopX * channels, afterWidth * channels)
                      .CopyTo(dstRow.Slice(chopX * channels));
            }

            dstY++;
        }

        return result;
    }

    // ─── Transpose ───────────────────────────────────────────────

    /// <summary>
    /// Transposes the image by reflecting about the main diagonal (top-left to bottom-right).
    /// Source pixel (x,y) maps to destination (y,x). Width and height are swapped.
    /// </summary>
    public static ImageFrame Transpose(ImageFrame source)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(srcHeight, srcWidth, source.Colorspace, source.HasAlpha);

        for (int y = 0; y < srcHeight; y++)
        {
            var srcRow = source.GetPixelRow(y);
            for (int x = 0; x < srcWidth; x++)
            {
                // (x,y) → (y,x)
                var dstRow = result.GetPixelRowForWrite(x);
                int srcOffset = x * channels;
                int dstOffset = y * channels;
                for (int c = 0; c < channels; c++)
                    dstRow[dstOffset + c] = srcRow[srcOffset + c];
            }
        }

        return result;
    }

    // ─── Transverse ──────────────────────────────────────────────

    /// <summary>
    /// Reflects the image about the anti-diagonal (top-right to bottom-left).
    /// Source pixel (x,y) maps to destination (H-1-y, W-1-x). Width and height are swapped.
    /// </summary>
    public static ImageFrame Transverse(ImageFrame source)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(srcHeight, srcWidth, source.Colorspace, source.HasAlpha);

        for (int y = 0; y < srcHeight; y++)
        {
            var srcRow = source.GetPixelRow(y);
            for (int x = 0; x < srcWidth; x++)
            {
                // (x,y) → (srcHeight-1-y, srcWidth-1-x) with swapped dimensions
                int dstX = srcHeight - 1 - y;
                int dstY = srcWidth - 1 - x;
                var dstRow = result.GetPixelRowForWrite(dstY);
                int srcOffset = x * channels;
                int dstOffset = dstX * channels;
                for (int c = 0; c < channels; c++)
                    dstRow[dstOffset + c] = srcRow[srcOffset + c];
            }
        }

        return result;
    }

    // ─── Deskew ──────────────────────────────────────────────────

    /// <summary>
    /// Detects and corrects image skew using a Radon transform projection analysis.
    /// Scans angles from -10° to +10° and rotates by the detected skew angle.
    /// Best results on scanned text documents or images with strong horizontal/vertical features.
    /// </summary>
    /// <param name="source">Source image.</param>
    /// <param name="threshold">Brightness threshold (0.0–1.0) separating foreground from background.
    /// Pixels below threshold are treated as foreground (text). Default 0.4 (40%).</param>
    /// <returns>Deskewed image rotated to correct the detected angle.</returns>
    public static ImageFrame Deskew(ImageFrame source, double threshold = 0.4)
    {
        double skewAngle = DetectSkewAngle(source, threshold);

        // If skew is negligible, return a copy
        if (Math.Abs(skewAngle) < 0.01)
        {
            return Crop(source, 0, 0, (int)source.Columns, (int)source.Rows);
        }

        return RotateArbitrary(source, -skewAngle);
    }

    /// <summary>
    /// Detects the skew angle of the image using Radon transform projection variance.
    /// </summary>
    private static double DetectSkewAngle(ImageFrame source, double threshold)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        ushort thresholdValue = (ushort)(threshold * Quantum.MaxValue);

        // Convert to binary: true = foreground (dark pixels like text)
        bool[] foreground = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                // Use luminance: avg of RGB channels
                int idx = x * channels;
                double lum = (row[idx] + row[idx + 1] + row[idx + 2]) / 3.0;
                foreground[y * width + x] = lum < thresholdValue;
            }
        }

        // Radon transform: project at angles from -10° to +10° in 0.1° steps
        double bestAngle = 0;
        double bestVariance = 0;

        for (double angle = -10.0; angle <= 10.0; angle += 0.1)
        {
            double radians = angle * Math.PI / 180.0;
            double cosA = Math.Cos(radians);
            double sinA = Math.Sin(radians);

            // Project foreground pixels onto the vertical axis (perpendicular to angle)
            // For horizontal text, projection onto Y axis gives peaks at text lines
            int projectionSize = height + width; // enough range for rotated projections
            int[] projection = new int[projectionSize];
            int offset = projectionSize / 2;

            int fgCount = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!foreground[y * width + x]) continue;
                    fgCount++;

                    // Project onto line perpendicular to angle direction
                    double projVal = (x - width / 2.0) * sinA + (y - height / 2.0) * cosA;
                    int bin = (int)(projVal + offset);
                    if (bin >= 0 && bin < projectionSize)
                        projection[bin]++;
                }
            }

            if (fgCount == 0) continue;

            // Calculate variance of the projection — higher = more aligned features
            double mean = (double)fgCount / projectionSize;
            double variance = 0;
            for (int i = 0; i < projectionSize; i++)
            {
                double diff = projection[i] - mean;
                variance += diff * diff;
            }
            variance /= projectionSize;

            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestAngle = angle;
            }
        }

        return bestAngle;
    }

    /// <summary>
    /// Rotates the image by an arbitrary angle (in degrees, clockwise). Uses bilinear interpolation. Background is
    /// transparent/black.
    /// </summary>
    public static ImageFrame RotateArbitrary(ImageFrame source, double angleDegrees)
    {
        double angleRad = angleDegrees * Math.PI / 180.0;
        double cosA = Math.Cos(angleRad);
        double sinA = Math.Sin(angleRad);

        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Calculate bounding box of rotated image
        double cx = srcWidth / 2.0;
        double cy = srcHeight / 2.0;

        double[] cornersX = [ 0 - cx, srcWidth - cx, srcWidth - cx, 0 - cx ];
        double[] cornersY = [ 0 - cy, 0 - cy, srcHeight - cy, srcHeight - cy ];

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        for (int i = 0;i < 4;i++)
        {
            double rx = cornersX[i] * cosA - cornersY[i] * sinA;
            double ry = cornersX[i] * sinA + cornersY[i] * cosA;
            minX = Math.Min(minX, rx);
            maxX = Math.Max(maxX, rx);
            minY = Math.Min(minY, ry);
            maxY = Math.Max(maxY, ry);
        }

        int newWidth = (int)Math.Ceiling(maxX - minX);
        int newHeight = (int)Math.Ceiling(maxY - minY);
        double newCx = newWidth / 2.0;
        double newCy = newHeight / 2.0;

        var result = new ImageFrame();
        result.Initialize(newWidth, newHeight, source.Colorspace, source.HasAlpha);

        // Inverse mapping: for each destination pixel, find source coordinate
        Parallel.For(0, newHeight, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            double dy = y - newCy;

            for (int x = 0;x < newWidth;x++)
            {
                double dx = x - newCx;

                // Inverse rotation
                double srcXf = dx * cosA + dy * sinA + cx;
                double srcYf = -dx * sinA + dy * cosA + cy;

                int dstOffset = x * channels;

                if (srcXf >= 0 && srcXf < srcWidth - 1 && srcYf >= 0 && srcYf < srcHeight - 1)
                {
                    // Bilinear interpolation
                    int sx0 = (int)srcXf;
                    int sy0 = (int)srcYf;
                    float fx = (float)(srcXf - sx0);
                    float fy = (float)(srcYf - sy0);

                    var row0 = source.GetPixelRow(sy0);
                    var row1 = source.GetPixelRow(sy0 + 1);

                    for (int c = 0;c < channels;c++)
                    {
                        float v00 = row0[sx0 * channels + c];
                        float v10 = row0[(sx0 + 1) * channels + c];
                        float v01 = row1[sx0 * channels + c];
                        float v11 = row1[(sx0 + 1) * channels + c];

                        float val = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy)
                                  + v01 * (1 - fx) * fy + v11 * fx * fy;
                        dstRow[dstOffset + c] = (ushort)Math.Clamp(val + 0.5f, 0, Quantum.MaxValue);
                    }
                }
                // Else: pixel stays at 0 (black/transparent)
            }
        });

        return result;
    }

    // ─── Shear ──────────────────────────────────────────────────────

    /// <summary>
    /// Applies a shear (skew) transformation using Paeth's 2-pass decomposition.
    /// </summary>
    /// <param name="xShear">Horizontal shear angle in degrees.</param>
    /// <param name="yShear">Vertical shear angle in degrees.</param>
    /// <param name="bgR">Background red (for empty areas).</param>
    /// <param name="bgG">Background green.</param>
    /// <param name="bgB">Background blue.</param>
    public static ImageFrame Shear(ImageFrame source, double xShear, double yShear,
        ushort bgR = 0, ushort bgG = 0, ushort bgB = 0)
    {
        double xShearTan = Math.Tan(xShear * Math.PI / 180.0);
        double yShearTan = Math.Tan(yShear * Math.PI / 180.0);

        // Pass 1: X-shear
        var xSheared = XShear(source, xShearTan, bgR, bgG, bgB);

        // Pass 2: Y-shear
        var result = YShear(xSheared, yShearTan, bgR, bgG, bgB);
        xSheared.Dispose();

        return result;
    }

    private static ImageFrame XShear(ImageFrame source, double shearFactor,
        ushort bgR, ushort bgG, ushort bgB)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        int expansion = (int)Math.Ceiling(Math.Abs(shearFactor) * srcHeight);
        int newWidth = srcWidth + expansion;

        var result = new ImageFrame();
        result.Initialize(newWidth, srcHeight, source.Colorspace, source.HasAlpha);

        // Fill with background
        for (int y = 0; y < srcHeight; y++)
        {
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < newWidth; x++)
            {
                int idx = x * channels;
                dstRow[idx] = bgR;
                dstRow[idx + 1] = bgG;
                dstRow[idx + 2] = bgB;
                if (channels == 4) dstRow[idx + 3] = Quantum.MaxValue;
            }
        }

        double centerY = srcHeight / 2.0;
        for (int y = 0; y < srcHeight; y++)
        {
            double displacement = shearFactor * (y - centerY);
            int step = (int)Math.Floor(displacement);
            double fractional = displacement - step;
            if (fractional < 0) { fractional += 1.0; step--; }

            int xOffset = (expansion / 2) + step;
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < srcWidth; x++)
            {
                int dstX = x + xOffset;
                if (dstX < 0 || dstX >= newWidth) continue;
                if (dstX + 1 >= newWidth)
                {
                    // No room for interpolation — just copy
                    int srcIdx = x * channels;
                    int dstIdx = dstX * channels;
                    dstRow[dstIdx] = srcRow[srcIdx];
                    dstRow[dstIdx + 1] = srcRow[srcIdx + 1];
                    dstRow[dstIdx + 2] = srcRow[srcIdx + 2];
                    if (channels == 4) dstRow[dstIdx + 3] = srcRow[srcIdx + 3];
                    continue;
                }

                // Subpixel interpolation
                int si = x * channels;
                int di = dstX * channels;
                int di2 = (dstX + 1) * channels;
                double oneMinusFrac = 1.0 - fractional;

                for (int c = 0; c < channels; c++)
                {
                    double val = srcRow[si + c];
                    double bg = (c == 0 ? bgR : c == 1 ? bgG : c == 2 ? bgB : Quantum.MaxValue);

                    // Current pixel gets (1-frac) of source
                    double curr = dstRow[di + c] * fractional + val * oneMinusFrac;
                    dstRow[di + c] = (ushort)Math.Clamp(curr, 0, Quantum.MaxValue);

                    // Next pixel gets frac of source bleed
                    double next = dstRow[di2 + c] * oneMinusFrac + val * fractional;
                    dstRow[di2 + c] = (ushort)Math.Clamp(next, 0, Quantum.MaxValue);
                }
            }
        }

        return result;
    }

    private static ImageFrame YShear(ImageFrame source, double shearFactor,
        ushort bgR, ushort bgG, ushort bgB)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        int expansion = (int)Math.Ceiling(Math.Abs(shearFactor) * srcWidth);
        int newHeight = srcHeight + expansion;

        var result = new ImageFrame();
        result.Initialize(srcWidth, newHeight, source.Colorspace, source.HasAlpha);

        // Fill with background
        for (int y = 0; y < newHeight; y++)
        {
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < srcWidth; x++)
            {
                int idx = x * channels;
                dstRow[idx] = bgR;
                dstRow[idx + 1] = bgG;
                dstRow[idx + 2] = bgB;
                if (channels == 4) dstRow[idx + 3] = Quantum.MaxValue;
            }
        }

        double centerX = srcWidth / 2.0;

        // Build column-based view for vertical shear
        // Since we don't have columnar access, we operate per-column
        for (int x = 0; x < srcWidth; x++)
        {
            double displacement = shearFactor * (x - centerX);
            int step = (int)Math.Floor(displacement);
            double fractional = displacement - step;
            if (fractional < 0) { fractional += 1.0; step--; }

            int yOffset = (expansion / 2) + step;

            for (int y = 0; y < srcHeight; y++)
            {
                int dstY = y + yOffset;
                if (dstY < 0 || dstY >= newHeight) continue;

                var srcRow = source.GetPixelRow(y);
                var dstRow = result.GetPixelRowForWrite(dstY);
                int si = x * channels;
                int di = x * channels;

                if (dstY + 1 >= newHeight)
                {
                    for (int c = 0; c < channels; c++)
                        dstRow[di + c] = srcRow[si + c];
                    continue;
                }

                var dstRow2 = result.GetPixelRowForWrite(dstY + 1);
                double oneMinusFrac = 1.0 - fractional;

                for (int c = 0; c < channels; c++)
                {
                    double val = srcRow[si + c];
                    double curr = dstRow[di + c] * fractional + val * oneMinusFrac;
                    dstRow[di + c] = (ushort)Math.Clamp(curr, 0, Quantum.MaxValue);

                    double next = dstRow2[di + c] * oneMinusFrac + val * fractional;
                    dstRow2[di + c] = (ushort)Math.Clamp(next, 0, Quantum.MaxValue);
                }
            }
        }

        return result;
    }

    // ─── Roll ──────────────────────────────────────────────────────────

    /// <summary>
    /// Circular pixel shift — wraps pixels that move past one edge to the opposite edge.
    /// </summary>
    public static ImageFrame Roll(ImageFrame source, int xOffset, int yOffset)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Normalize offsets to [0, dimension)
        xOffset = ((xOffset % width) + width) % width;
        yOffset = ((yOffset % height) + height) % height;

        if (xOffset == 0 && yOffset == 0)
        {
            // No shift — clone
            var clone = new ImageFrame();
            clone.Initialize(width, height, source.Colorspace, source.HasAlpha);
            for (int y = 0; y < height; y++)
            {
                source.GetPixelRow(y).CopyTo(clone.GetPixelRowForWrite(y));
            }
            return clone;
        }

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, srcY =>
        {
            int dstY = (srcY + yOffset) % height;
            var srcRow = source.GetPixelRow(srcY);
            var dstRow = result.GetPixelRowForWrite(dstY);

            if (xOffset == 0)
            {
                srcRow.CopyTo(dstRow);
            }
            else
            {
                // Copy right portion (from xOffset..width) to beginning
                int rightLen = (width - xOffset) * channels;
                int leftLen = xOffset * channels;
                srcRow.Slice(0, rightLen).CopyTo(dstRow.Slice(xOffset * channels));
                srcRow.Slice(rightLen).CopyTo(dstRow.Slice(0, leftLen));
            }
        });

        return result;
    }

    // ─── Splice ────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts blank space into an image (the inverse of Chop). Adds a rectangular region
    /// filled with the specified color at the given position, pushing existing pixels outward.
    /// </summary>
    public static ImageFrame Splice(ImageFrame source, int insertX, int insertY,
        int insertWidth, int insertHeight,
        ushort fillR = 0, ushort fillG = 0, ushort fillB = 0, ushort fillA = 65535)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        insertX = Math.Clamp(insertX, 0, srcWidth);
        insertY = Math.Clamp(insertY, 0, srcHeight);

        int newWidth = srcWidth + insertWidth;
        int newHeight = srcHeight + insertHeight;

        var result = new ImageFrame();
        result.Initialize(newWidth, newHeight, source.Colorspace, source.HasAlpha);

        // Fill the entire result with the fill color first
        for (int y = 0; y < newHeight; y++)
        {
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < newWidth; x++)
            {
                int off = x * channels;
                dstRow[off] = fillR;
                if (channels > 1) dstRow[off + 1] = fillG;
                if (channels > 2) dstRow[off + 2] = fillB;
                if (channels > 3) dstRow[off + 3] = fillA;
            }
        }

        // Copy top-left quadrant: source rows [0..insertY), columns [0..insertX)
        Parallel.For(0, insertY, srcY =>
        {
            var srcRow = source.GetPixelRow(srcY);
            var dstRow = result.GetPixelRowForWrite(srcY);
            srcRow.Slice(0, insertX * channels).CopyTo(dstRow);
        });

        // Copy top-right quadrant: source rows [0..insertY), columns [insertX..srcWidth)
        Parallel.For(0, insertY, srcY =>
        {
            var srcRow = source.GetPixelRow(srcY);
            var dstRow = result.GetPixelRowForWrite(srcY);
            srcRow.Slice(insertX * channels, (srcWidth - insertX) * channels)
                  .CopyTo(dstRow.Slice((insertX + insertWidth) * channels));
        });

        // Copy bottom-left quadrant: source rows [insertY..srcHeight), columns [0..insertX)
        Parallel.For(insertY, srcHeight, srcY =>
        {
            var srcRow = source.GetPixelRow(srcY);
            var dstRow = result.GetPixelRowForWrite(srcY + insertHeight);
            srcRow.Slice(0, insertX * channels).CopyTo(dstRow);
        });

        // Copy bottom-right quadrant: source rows [insertY..srcHeight), columns [insertX..srcWidth)
        Parallel.For(insertY, srcHeight, srcY =>
        {
            var srcRow = source.GetPixelRow(srcY);
            var dstRow = result.GetPixelRowForWrite(srcY + insertHeight);
            srcRow.Slice(insertX * channels, (srcWidth - insertX) * channels)
                  .CopyTo(dstRow.Slice((insertX + insertWidth) * channels));
        });

        return result;
    }

    // ─── AutoOrient ────────────────────────────────────────────────────

    /// <summary>
    /// Applies the EXIF orientation tag to produce a correctly-oriented image,
    /// then resets the orientation to TopLeft. This is the physical rotation/flip
    /// that corresponds to the stored tag value.
    /// </summary>
    public static ImageFrame AutoOrient(ImageFrame source)
    {
        var orientation = source.Orientation;
        if (orientation == OrientationType.Undefined || orientation == OrientationType.TopLeft)
        {
            // Already correct — return clone
            int w = (int)source.Columns, h = (int)source.Rows;
            var clone = new ImageFrame();
            clone.Initialize(w, h, source.Colorspace, source.HasAlpha);
            for (int y = 0; y < h; y++)
                source.GetPixelRow(y).CopyTo(clone.GetPixelRowForWrite(y));
            clone.Orientation = OrientationType.TopLeft;
            return clone;
        }

        ImageFrame result = orientation switch
        {
            OrientationType.TopRight => Flop(source),
            OrientationType.BottomRight => Rotate(source, RotationAngle.Rotate180),
            OrientationType.BottomLeft => Flip(source),
            OrientationType.LeftTop => Transpose(source),
            OrientationType.RightTop => Rotate(source, RotationAngle.Rotate90),
            OrientationType.RightBottom => Transverse(source),
            OrientationType.LeftBottom => Rotate(source, RotationAngle.Rotate270),
            _ => throw new ArgumentException($"Unknown orientation: {orientation}")
        };

        result.Orientation = OrientationType.TopLeft;
        return result;
    }

    /// <summary>
    /// Split an image into a grid of tiles. Returns a list of ImageFrames, row-major order.
    /// If the image doesn't divide evenly, edge tiles will be smaller.
    /// </summary>
    /// <param name="source">Source image to split.</param>
    /// <param name="tileWidth">Width of each tile in pixels.</param>
    /// <param name="tileHeight">Height of each tile in pixels.</param>
    public static List<ImageFrame> CropToTiles(ImageFrame source, int tileWidth, int tileHeight)
    {
        if (tileWidth <= 0 || tileHeight <= 0)
            throw new ArgumentOutOfRangeException("Tile dimensions must be positive.");

        int srcW = (int)source.Columns;
        int srcH = (int)source.Rows;
        int cols = (srcW + tileWidth - 1) / tileWidth;
        int rows = (srcH + tileHeight - 1) / tileHeight;

        var tiles = new List<ImageFrame>(rows * cols);

        for (int ty = 0; ty < rows; ty++)
        {
            int y0 = ty * tileHeight;
            int h = Math.Min(tileHeight, srcH - y0);

            for (int tx = 0; tx < cols; tx++)
            {
                int x0 = tx * tileWidth;
                int w = Math.Min(tileWidth, srcW - x0);
                tiles.Add(Crop(source, x0, y0, w, h));
            }
        }

        return tiles;
    }
}

/// <summary>
/// Standard rotation angles (multiples of 90°).
/// </summary>
public enum RotationAngle
{
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270
}
