using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace SharpImage.Effects;

/// <summary>
/// Decorative and paint operations: Border, Frame, Raise, Shade, OpaquePaint, TransparentPaint.
/// Based on ImageMagick's decorate.c, effect.c, and paint.c algorithms.
/// </summary>
public static class DecorateOps
{
    // ─── Border ─────────────────────────────────────────────────────

    /// <summary>
    /// Adds a solid-color border around the image, extending the canvas.
    /// </summary>
    public static ImageFrame Border(ImageFrame source, int borderWidth, int borderHeight,
        ushort borderR = 0, ushort borderG = 0, ushort borderB = 0)
    {
        if (borderWidth < 0 || borderHeight < 0)
            throw new ArgumentException("Border dimensions must be non-negative.");

        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int newWidth = srcWidth + borderWidth * 2;
        int newHeight = srcHeight + borderHeight * 2;

        var result = new ImageFrame();
        result.Initialize(newWidth, newHeight, source.Colorspace, source.HasAlpha);

        // Fill entire canvas with border color
        Parallel.For(0, newHeight, y =>
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = 0; x < newWidth; x++)
            {
                int idx = x * channels;
                row[idx] = borderR;
                row[idx + 1] = borderG;
                row[idx + 2] = borderB;
                if (channels == 4) row[idx + 3] = Quantum.MaxValue;
            }
        });

        // Composite source at center
        for (int y = 0; y < srcHeight; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y + borderHeight);
            srcRow.CopyTo(dstRow.Slice(borderWidth * channels));
        }

        return result;
    }

    // ─── Frame ──────────────────────────────────────────────────────

    private const double HighlightModulate = 125.0 / 255.0;
    private const double ShadowModulate = 135.0 / 255.0;
    private const double AccentuateModulate = 80.0 / 255.0;
    private const double TroughModulate = 110.0 / 255.0;

    /// <summary>
    /// Adds a 3D beveled frame around the image.
    /// The frame consists of outer bevel + matte border + inner bevel.
    /// </summary>
    public static ImageFrame Frame(ImageFrame source, int matteWidth, int matteHeight,
        int outerBevel, int innerBevel,
        ushort matteR = 32768, ushort matteG = 32768, ushort matteB = 32768)
    {
        if (outerBevel < 0 || innerBevel < 0 || matteWidth < 0 || matteHeight < 0)
            throw new ArgumentException("Frame dimensions must be non-negative.");

        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int bevelWidth = outerBevel + innerBevel;
        int newWidth = srcWidth + (matteWidth + bevelWidth) * 2;
        int newHeight = srcHeight + (matteHeight + bevelWidth) * 2;

        double matteNormR = matteR * Quantum.Scale;
        double matteNormG = matteG * Quantum.Scale;
        double matteNormB = matteB * Quantum.Scale;

        // Precompute modulated colors
        ushort highlightR = Clamp((1.0 - HighlightModulate) * matteNormR + HighlightModulate);
        ushort highlightG = Clamp((1.0 - HighlightModulate) * matteNormG + HighlightModulate);
        ushort highlightB = Clamp((1.0 - HighlightModulate) * matteNormB + HighlightModulate);

        ushort shadowR = Clamp(matteNormR * (1.0 - ShadowModulate));
        ushort shadowG = Clamp(matteNormG * (1.0 - ShadowModulate));
        ushort shadowB = Clamp(matteNormB * (1.0 - ShadowModulate));

        ushort accentR = Clamp((1.0 - AccentuateModulate) * matteNormR + AccentuateModulate);
        ushort accentG = Clamp((1.0 - AccentuateModulate) * matteNormG + AccentuateModulate);
        ushort accentB = Clamp((1.0 - AccentuateModulate) * matteNormB + AccentuateModulate);

        ushort troughR = Clamp(matteNormR * (1.0 - TroughModulate));
        ushort troughG = Clamp(matteNormG * (1.0 - TroughModulate));
        ushort troughB = Clamp(matteNormB * (1.0 - TroughModulate));

        var result = new ImageFrame();
        result.Initialize(newWidth, newHeight, source.Colorspace, source.HasAlpha);

        int imageLeft = matteWidth + bevelWidth;
        int imageTop = matteHeight + bevelWidth;
        int imageRight = imageLeft + srcWidth;
        int imageBottom = imageTop + srcHeight;

        Parallel.For(0, newHeight, y =>
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = 0; x < newWidth; x++)
            {
                int idx = x * channels;
                ushort r, g, b;

                if (y >= imageTop && y < imageBottom && x >= imageLeft && x < imageRight)
                {
                    // Source image area — will be composited later
                    r = matteR; g = matteG; b = matteB;
                }
                else
                {
                    // Determine 3D bevel zone
                    GetFramePixel(x, y, newWidth, newHeight,
                        outerBevel, innerBevel, matteWidth, matteHeight,
                        imageLeft, imageTop, imageRight, imageBottom,
                        matteR, matteG, matteB,
                        highlightR, highlightG, highlightB,
                        shadowR, shadowG, shadowB,
                        accentR, accentG, accentB,
                        troughR, troughG, troughB,
                        out r, out g, out b);
                }

                row[idx] = r;
                row[idx + 1] = g;
                row[idx + 2] = b;
                if (channels == 4) row[idx + 3] = Quantum.MaxValue;
            }
        });

        // Composite source
        for (int y = 0; y < srcHeight; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y + imageTop);
            srcRow.CopyTo(dstRow.Slice(imageLeft * channels));
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetFramePixel(int x, int y, int width, int height,
        int outerBevel, int innerBevel, int matteWidth, int matteHeight,
        int imgLeft, int imgTop, int imgRight, int imgBottom,
        ushort matteR, ushort matteG, ushort matteB,
        ushort hlR, ushort hlG, ushort hlB,
        ushort shR, ushort shG, ushort shB,
        ushort acR, ushort acG, ushort acB,
        ushort trR, ushort trG, ushort trB,
        out ushort r, out ushort g, out ushort b)
    {
        // Outer bevel: top-left = highlight, bottom-right = shadow
        if (x < outerBevel || y < outerBevel ||
            x >= width - outerBevel || y >= height - outerBevel)
        {
            bool isTopLeft = (x < y) ? (x < outerBevel) : (y < outerBevel);
            bool isBottomRight = (x >= width - outerBevel) || (y >= height - outerBevel);
            bool topOrLeft = y < outerBevel || x < outerBevel;

            if (topOrLeft && !isBottomRight)
            {
                r = hlR; g = hlG; b = hlB;
            }
            else
            {
                r = shR; g = shG; b = shB;
            }
            return;
        }

        // Inner bevel: shadow on top-left (recessed look), highlight on bottom-right
        if (y >= imgTop - innerBevel && y < imgBottom + innerBevel &&
            x >= imgLeft - innerBevel && x < imgRight + innerBevel)
        {
            bool topOrLeft = (y < imgTop) || (x < imgLeft);
            if (topOrLeft)
            {
                r = trR; g = trG; b = trB;
            }
            else
            {
                r = acR; g = acG; b = acB;
            }
            return;
        }

        // Matte region
        r = matteR; g = matteG; b = matteB;
    }

    // ─── Raise ──────────────────────────────────────────────────────

    private const double HighlightFactor = 190.0 / 255.0;
    private const double AccentuateFactor = 135.0 / 255.0;
    private const double ShadowFactor = 190.0 / 255.0;
    private const double TroughFactor = 135.0 / 255.0;

    /// <summary>
    /// Applies a 3D raised or sunken button effect to the image edges (in-place modulation).
    /// </summary>
    /// <param name="raise">True for raised (light edges), false for sunken (dark edges).</param>
    public static ImageFrame Raise(ImageFrame source, int raiseWidth, int raiseHeight, bool raise = true)
    {
        if (raiseWidth < 0 || raiseHeight < 0)
            throw new ArgumentException("Raise dimensions must be non-negative.");

        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        raiseWidth = Math.Min(raiseWidth, srcWidth / 2);
        raiseHeight = Math.Min(raiseHeight, srcHeight / 2);

        var result = new ImageFrame();
        result.Initialize(srcWidth, srcHeight, source.Colorspace, source.HasAlpha);

        // Copy source
        for (int y = 0; y < srcHeight; y++)
        {
            source.GetPixelRow(y).CopyTo(result.GetPixelRowForWrite(y));
        }

        // Apply edge modulation
        for (int y = 0; y < srcHeight; y++)
        {
            var row = result.GetPixelRowForWrite(y);

            for (int x = 0; x < srcWidth; x++)
            {
                double factor = GetRaiseFactor(x, y, srcWidth, srcHeight, raiseWidth, raiseHeight, raise);
                if (factor == 1.0) continue;

                int idx = x * channels;
                row[idx] = ModulateRaise(row[idx], factor);
                row[idx + 1] = ModulateRaise(row[idx + 1], factor);
                row[idx + 2] = ModulateRaise(row[idx + 2], factor);
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GetRaiseFactor(int x, int y, int width, int height,
        int raiseWidth, int raiseHeight, bool raise)
    {
        bool inTopBand = y < raiseHeight;
        bool inBottomBand = y >= height - raiseHeight;
        bool inLeftBand = x < raiseWidth;
        bool inRightBand = x >= width - raiseWidth;

        if (!inTopBand && !inBottomBand && !inLeftBand && !inRightBand)
            return 1.0; // Interior — no modification

        double hlFactor = raise ? (1.0 + HighlightFactor) : (1.0 - ShadowFactor);
        double shFactor = raise ? (1.0 - ShadowFactor) : (1.0 + HighlightFactor);
        double acFactor = raise ? (1.0 + AccentuateFactor * 0.5) : (1.0 - TroughFactor * 0.5);
        double trFactor = raise ? (1.0 - TroughFactor * 0.5) : (1.0 + AccentuateFactor * 0.5);

        if (inTopBand)
        {
            if (inLeftBand) return hlFactor;
            if (inRightBand) return shFactor;
            return acFactor;
        }

        if (inBottomBand)
        {
            if (inLeftBand) return hlFactor;
            if (inRightBand) return shFactor;
            return trFactor;
        }

        // Side bands only
        if (inLeftBand) return hlFactor;
        return shFactor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ModulateRaise(ushort value, double factor)
    {
        double result = value * factor;
        if (result < 0) return 0;
        if (result > Quantum.MaxValue) return Quantum.MaxValue;
        return (ushort)result;
    }

    // ─── Shade ──────────────────────────────────────────────────────

    /// <summary>
    /// Applies a shading effect using a directional light source.
    /// Uses surface normal estimation from pixel gradients for Lambertian reflectance.
    /// </summary>
    /// <param name="gray">If true, modulates pixel intensity by shade. If false, outputs shade as grayscale.</param>
    /// <param name="azimuthDegrees">Light direction angle in degrees (0 = right, 90 = top).</param>
    /// <param name="elevationDegrees">Light elevation angle in degrees (0 = horizon, 90 = directly above).</param>
    public static ImageFrame Shade(ImageFrame source, bool gray, double azimuthDegrees, double elevationDegrees)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        double azimuth = azimuthDegrees * Math.PI / 180.0;
        double elevation = elevationDegrees * Math.PI / 180.0;

        double lightX = Quantum.MaxValue * Math.Cos(azimuth) * Math.Cos(elevation);
        double lightY = Quantum.MaxValue * Math.Sin(azimuth) * Math.Cos(elevation);
        double lightZ = Quantum.MaxValue * Math.Sin(elevation);

        var result = new ImageFrame();
        result.Initialize(srcWidth, srcHeight, source.Colorspace, source.HasAlpha);

        for (int y = 0; y < srcHeight; y++)
        {
            var row = result.GetPixelRowForWrite(y);

            for (int x = 0; x < srcWidth; x++)
            {
                // Get intensity of surrounding pixels for gradient
                double center = GetIntensity(source, x, y);
                double left = GetIntensity(source, x - 1, y);
                double right = GetIntensity(source, x + 1, y);
                double top = GetIntensity(source, x, y - 1);
                double bottom = GetIntensity(source, x, y + 1);

                // Surface normal from gradient
                double normalX = left - right;
                double normalY = top - bottom;
                double normalZ = 2.0 * Quantum.MaxValue;

                // Lambertian shade: dot product of normal and light direction
                double shade;
                double normalMagSq = normalX * normalX + normalY * normalY + normalZ * normalZ;
                if (Math.Abs(normalX) < 0.5 && Math.Abs(normalY) < 0.5)
                {
                    shade = lightZ;
                }
                else if (normalMagSq > 0.0001)
                {
                    double dot = normalX * lightX + normalY * lightY + normalZ * lightZ;
                    shade = dot / Math.Sqrt(normalMagSq);
                }
                else
                {
                    shade = 0;
                }

                shade = Math.Clamp(shade, 0, Quantum.MaxValue);

                int idx = x * channels;
                if (gray)
                {
                    // Modulate original pixel by shade
                    double scale = shade * Quantum.Scale;
                    var srcRow = source.GetPixelRow(y);
                    row[idx] = (ushort)Math.Clamp(srcRow[idx] * scale, 0, Quantum.MaxValue);
                    row[idx + 1] = (ushort)Math.Clamp(srcRow[idx + 1] * scale, 0, Quantum.MaxValue);
                    row[idx + 2] = (ushort)Math.Clamp(srcRow[idx + 2] * scale, 0, Quantum.MaxValue);
                }
                else
                {
                    ushort s = (ushort)shade;
                    row[idx] = s;
                    row[idx + 1] = s;
                    row[idx + 2] = s;
                }

                if (channels == 4)
                {
                    var srcRow = source.GetPixelRow(y);
                    row[idx + 3] = srcRow[idx + 3];
                }
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GetIntensity(ImageFrame frame, int x, int y)
    {
        x = Math.Clamp(x, 0, (int)frame.Columns - 1);
        y = Math.Clamp(y, 0, (int)frame.Rows - 1);
        var row = frame.GetPixelRow(y);
        int ch = frame.NumberOfChannels;
        int idx = x * ch;
        // Rec. 709 luminance
        return 0.2126 * row[idx] + 0.7152 * row[idx + 1] + 0.0722 * row[idx + 2];
    }

    // ─── Opaque Paint ───────────────────────────────────────────────

    /// <summary>
    /// Replaces all pixels matching a target color (within fuzz tolerance) with a fill color.
    /// </summary>
    /// <param name="fuzz">Color distance tolerance (0 = exact match, higher = more permissive).</param>
    /// <param name="invert">If true, replaces pixels that do NOT match.</param>
    public static ImageFrame OpaquePaint(ImageFrame source,
        ushort targetR, ushort targetG, ushort targetB,
        ushort fillR, ushort fillG, ushort fillB,
        double fuzz = 0, bool invert = false)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;
        double fuzzSquared = fuzz * fuzz;

        var result = new ImageFrame();
        result.Initialize(srcWidth, srcHeight, source.Colorspace, source.HasAlpha);

        for (int y = 0; y < srcHeight; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            srcRow.CopyTo(dstRow);

            for (int x = 0; x < srcWidth; x++)
            {
                int idx = x * channels;
                double dr = srcRow[idx] - targetR;
                double dg = srcRow[idx + 1] - targetG;
                double db = srcRow[idx + 2] - targetB;
                double distSq = dr * dr + dg * dg + db * db;

                bool matches = distSq <= fuzzSquared;
                if (invert) matches = !matches;

                if (matches)
                {
                    dstRow[idx] = fillR;
                    dstRow[idx + 1] = fillG;
                    dstRow[idx + 2] = fillB;
                }
            }
        }

        return result;
    }

    // ─── Transparent Paint ──────────────────────────────────────────

    /// <summary>
    /// Makes all pixels matching a target color (within fuzz) transparent.
    /// Returns an image with alpha channel.
    /// </summary>
    /// <param name="opacity">Alpha value to set for matching pixels (0 = fully transparent).</param>
    public static ImageFrame TransparentPaint(ImageFrame source,
        ushort targetR, ushort targetG, ushort targetB,
        double fuzz = 0, ushort opacity = 0, bool invert = false)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        double fuzzSquared = fuzz * fuzz;

        // Result must have alpha
        var result = new ImageFrame();
        result.Initialize(srcWidth, srcHeight, source.Colorspace, hasAlpha: true);
        int dstChannels = result.NumberOfChannels; // 4

        for (int y = 0; y < srcHeight; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            int srcChannels = source.NumberOfChannels;

            for (int x = 0; x < srcWidth; x++)
            {
                int srcIdx = x * srcChannels;
                int dstIdx = x * dstChannels;

                dstRow[dstIdx] = srcRow[srcIdx];
                dstRow[dstIdx + 1] = srcRow[srcIdx + 1];
                dstRow[dstIdx + 2] = srcRow[srcIdx + 2];
                dstRow[dstIdx + 3] = srcChannels == 4 ? srcRow[srcIdx + 3] : Quantum.MaxValue;

                double dr = srcRow[srcIdx] - targetR;
                double dg = srcRow[srcIdx + 1] - targetG;
                double db = srcRow[srcIdx + 2] - targetB;
                double distSq = dr * dr + dg * dg + db * db;

                bool matches = distSq <= fuzzSquared;
                if (invert) matches = !matches;

                if (matches)
                {
                    dstRow[dstIdx + 3] = opacity;
                }
            }
        }

        return result;
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Clamp(double normalized)
    {
        double val = normalized * Quantum.MaxValue;
        if (val < 0) return 0;
        if (val > Quantum.MaxValue) return Quantum.MaxValue;
        return (ushort)val;
    }
}
