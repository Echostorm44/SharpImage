// SharpImage — Alpha channel operations: extract, remove, set, make opaque/transparent.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Channel;

/// <summary>
/// Operations on the alpha (transparency) channel of an image.
/// </summary>
public static class AlphaOps
{
    /// <summary>
    /// Extracts the alpha channel as a grayscale image. Fully opaque → white, fully transparent → black.
    /// If the image has no alpha, returns an all-white grayscale image.
    /// </summary>
    public static ImageFrame Extract(ImageFrame source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int srcChannels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.SRGB, hasAlpha: false);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort alpha = source.HasAlpha ? srcRow[x * srcChannels + srcChannels - 1] : Quantum.MaxValue;
                int dstOff = x * 3;
                dstRow[dstOff] = alpha;
                dstRow[dstOff + 1] = alpha;
                dstRow[dstOff + 2] = alpha;
            }
        });

        return result;
    }

    /// <summary>
    /// Removes the alpha channel, compositing over the specified background color (default: white).
    /// Returns an opaque RGB image.
    /// </summary>
    public static ImageFrame Remove(ImageFrame source, ushort bgR = Quantum.MaxValue, ushort bgG = Quantum.MaxValue, ushort bgB = Quantum.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(source);
        int width = (int)source.Columns;
        int height = (int)source.Rows;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, hasAlpha: false);

        if (!source.HasAlpha)
        {
            // No alpha — just copy RGB
            for (int y = 0; y < height; y++)
            {
                var srcRow = source.GetPixelRow(y);
                var dstRow = result.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    int srcOff = x * source.NumberOfChannels;
                    int dstOff = x * 3;
                    dstRow[dstOff] = srcRow[srcOff];
                    dstRow[dstOff + 1] = srcRow[srcOff + 1];
                    dstRow[dstOff + 2] = srcRow[srcOff + 2];
                }
            }
            return result;
        }

        double invMax = Quantum.Scale;
        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            int srcCh = source.NumberOfChannels;
            for (int x = 0; x < width; x++)
            {
                int srcOff = x * srcCh;
                double alpha = srcRow[srcOff + 3] * invMax;
                double invAlpha = 1.0 - alpha;

                dstRow[x * 3] = Quantum.Clamp((int)Math.Round(srcRow[srcOff] * alpha + bgR * invAlpha));
                dstRow[x * 3 + 1] = Quantum.Clamp((int)Math.Round(srcRow[srcOff + 1] * alpha + bgG * invAlpha));
                dstRow[x * 3 + 2] = Quantum.Clamp((int)Math.Round(srcRow[srcOff + 2] * alpha + bgB * invAlpha));
            }
        });

        return result;
    }

    /// <summary>
    /// Adds an alpha channel to the image if it doesn't have one, setting all pixels to the specified opacity.
    /// If the image already has an alpha channel, sets all alpha values to the specified value.
    /// </summary>
    public static ImageFrame SetAlpha(ImageFrame source, ushort alphaValue = Quantum.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(source);
        int width = (int)source.Columns;
        int height = (int)source.Rows;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, hasAlpha: true);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            int srcCh = source.NumberOfChannels;
            for (int x = 0; x < width; x++)
            {
                int srcOff = x * srcCh;
                int dstOff = x * 4;
                dstRow[dstOff] = srcRow[srcOff];
                dstRow[dstOff + 1] = srcRow[srcOff + 1];
                dstRow[dstOff + 2] = srcRow[srcOff + 2];
                dstRow[dstOff + 3] = alphaValue;
            }
        });

        return result;
    }

    /// <summary>
    /// Makes all pixels fully opaque by setting alpha = max. If image has no alpha, adds one.
    /// </summary>
    public static ImageFrame MakeOpaque(ImageFrame source) => SetAlpha(source, Quantum.MaxValue);

    /// <summary>
    /// Makes all matching pixels transparent. Pixels within the color tolerance of the target
    /// color have their alpha set to 0; all others are unchanged.
    /// </summary>
    public static ImageFrame MakeTransparent(ImageFrame source, ushort targetR, ushort targetG, ushort targetB, double fuzz = 0.05)
    {
        ArgumentNullException.ThrowIfNull(source);
        int width = (int)source.Columns;
        int height = (int)source.Rows;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, hasAlpha: true);

        double fuzzSq = fuzz * fuzz * Quantum.MaxValue * Quantum.MaxValue * 3.0;

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            int srcCh = source.NumberOfChannels;
            for (int x = 0; x < width; x++)
            {
                int srcOff = x * srcCh;
                int dstOff = x * 4;
                ushort r = srcRow[srcOff];
                ushort g = srcRow[srcOff + 1];
                ushort b = srcRow[srcOff + 2];

                dstRow[dstOff] = r;
                dstRow[dstOff + 1] = g;
                dstRow[dstOff + 2] = b;

                double dr = r - targetR;
                double dg = g - targetG;
                double db = b - targetB;
                double distSq = dr * dr + dg * dg + db * db;

                if (distSq <= fuzzSq)
                {
                    dstRow[dstOff + 3] = 0;
                }
                else
                {
                    dstRow[dstOff + 3] = source.HasAlpha ? srcRow[srcOff + 3] : Quantum.MaxValue;
                }
            }
        });

        return result;
    }
}
