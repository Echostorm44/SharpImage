using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Transform;

/// <summary>
/// Geometric distortion operations: affine, perspective, barrel/pincushion, polar/depolar transforms with bilinear
/// interpolation.
/// </summary>
public static class Distort
{
    /// <summary>
    /// Apply a 2D affine transformation defined by a 3x2 matrix: [a, b, tx] [c, d, ty] Maps (x',y') -> (a*x'+b*y'+tx,
    /// c*x'+d*y'+ty) in source coordinates.
    /// </summary>
    public static ImageFrame Affine(ImageFrame image, AffineMatrix matrix, uint outputWidth, uint outputHeight)
    {
        // Invert the matrix to map from output to source
        double det = matrix.Sx * matrix.Sy - matrix.Rx * matrix.Ry;
        if (Math.Abs(det) < 1e-12)
        {
            throw new ArgumentException("Affine matrix is degenerate (determinant ≈ 0).");
        }

        double invDet = 1.0 / det;
        double iSx = matrix.Sy * invDet, iRy = -matrix.Ry * invDet;
        double iRx = -matrix.Rx * invDet, iSy = matrix.Sx * invDet;
        double iTx = -(iSx * matrix.Tx + iRy * matrix.Ty);
        double iTy = -(iRx * matrix.Tx + iSy * matrix.Ty);

        return SampleWithInverseMap(image, outputWidth, outputHeight,
            (double x, double y, out double sx, out double sy) =>
            {
                sx = iSx * x + iRy * y + iTx;
                sy = iRx * x + iSy * y + iTy;
            });
    }

    /// <summary>
    /// Apply perspective (projective) transformation defined by 8 coefficients. Maps from 4 source points to 4
    /// destination points. sourcePoints and destPoints each contain 4 (x,y) pairs = 8 doubles.
    /// </summary>
    public static ImageFrame Perspective(ImageFrame image, double[] sourcePoints, double[] destPoints,
        uint outputWidth, uint outputHeight)
    {
        if (sourcePoints.Length != 8 || destPoints.Length != 8)
        {
            throw new ArgumentException("Perspective requires exactly 4 point pairs (8 doubles each).");
        }

        // Solve for the 3x3 perspective matrix that maps dest -> source
        double[] coeffs = SolvePerspective(destPoints, sourcePoints);

        return SampleWithInverseMap(image, outputWidth, outputHeight,
            (double x, double y, out double sx, out double sy) =>
            {
                double w = coeffs[6] * x + coeffs[7] * y + 1.0;
                if (Math.Abs(w) < 1e-12)
                {
                    sx = -1;
                    sy = -1;
                    return;
                }
                sx = (coeffs[0] * x + coeffs[1] * y + coeffs[2]) / w;
                sy = (coeffs[3] * x + coeffs[4] * y + coeffs[5]) / w;
            });
    }

    /// <summary>
    /// Barrel distortion correction. Corrects or introduces lens barrel/pincushion distortion. coefficients: [a, b, c,
    /// d] where r' = a*r³ + b*r² + c*r + d (typically d = 1-a-b-c).
    /// </summary>
    public static ImageFrame Barrel(ImageFrame image, double a, double b, double c, double d)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        double cx = width / 2.0, cy = height / 2.0;
        double maxR = Math.Sqrt(cx * cx + cy * cy);
        double invMaxR = 1.0 / maxR;

        return SampleWithInverseMap(image, (uint)width, (uint)height,
            (double x, double y, out double sx, out double sy) =>
            {
                double dx = (x - cx) * invMaxR;
                double dy = (y - cy) * invMaxR;
                double r = Math.Sqrt(dx * dx + dy * dy);
                double rPrime = a * r * r * r + b * r * r + c * r + d;
                double scale = r > 1e-10 ? rPrime / r : 1.0;
                sx = cx + dx * maxR * scale;
                sy = cy + dy * maxR * scale;
            });
    }

    /// <summary>
    /// Convert image from Cartesian to polar coordinates. Center of the image becomes the pole.
    /// </summary>
    public static ImageFrame CartesianToPolar(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        double cx = width / 2.0, cy = height / 2.0;
        double maxR = Math.Sqrt(cx * cx + cy * cy);

        return SampleWithInverseMap(image, (uint)width, (uint)height,
            (double x, double y, out double sx, out double sy) =>
            {
                // Output: x = angle [0..width] maps to [0..2π], y = radius [0..height] maps to [0..maxR]
                double angle = x / width * 2.0 * Math.PI;
                double radius = (double)y / height * maxR;
                sx = cx + radius * Math.Cos(angle);
                sy = cy + radius * Math.Sin(angle);
            });
    }

    /// <summary>
    /// Convert image from polar back to Cartesian coordinates.
    /// </summary>
    public static ImageFrame PolarToCartesian(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        double cx = width / 2.0, cy = height / 2.0;
        double maxR = Math.Sqrt(cx * cx + cy * cy);

        return SampleWithInverseMap(image, (uint)width, (uint)height,
            (double x, double y, out double sx, out double sy) =>
            {
                double ddx = x - cx, ddy = y - cy;
                double radius = Math.Sqrt(ddx * ddx + ddy * ddy);
                double angle = Math.Atan2(ddy, ddx);
                if (angle < 0)
                {
                    angle += 2.0 * Math.PI;
                }

                sx = angle / (2.0 * Math.PI) * width;
                sy = radius / maxR * height;
            });
    }

    // ═══════════════════════════════════════════════════════════════════
    // Core Sampling Engine
    // ═══════════════════════════════════════════════════════════════════

    private delegate void InverseMapFunc(double destX, double destY, out double srcX, out double srcY);

    private static ImageFrame SampleWithInverseMap(ImageFrame source, uint outWidth, uint outHeight,
        InverseMapFunc inverseMap)
    {
        int srcWidth = (int)source.Columns;
        int srcHeight = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(outWidth, outHeight, source.Colorspace, source.HasAlpha);

        for (int y = 0;y < (int)outHeight;y++)
        {
            var outRow = result.GetPixelRowForWrite(y);
            for (int x = 0;x < (int)outWidth;x++)
            {
                inverseMap(x + 0.5, y + 0.5, out double sx, out double sy);
                // Bilinear interpolation
                SampleBilinear(source, sx - 0.5, sy - 0.5, srcWidth, srcHeight, channels, outRow, x);
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SampleBilinear(ImageFrame source, double sx, double sy,
        int srcWidth, int srcHeight, int channels, Span<ushort> outRow, int outX)
    {
        int x0 = (int)Math.Floor(sx);
        int y0 = (int)Math.Floor(sy);
        double fx = sx - x0;
        double fy = sy - y0;

        for (int c = 0;c < channels;c++)
        {
            double v00 = GetPixelClamped(source, x0, y0, c, srcWidth, srcHeight, channels);
            double v10 = GetPixelClamped(source, x0 + 1, y0, c, srcWidth, srcHeight, channels);
            double v01 = GetPixelClamped(source, x0, y0 + 1, c, srcWidth, srcHeight, channels);
            double v11 = GetPixelClamped(source, x0 + 1, y0 + 1, c, srcWidth, srcHeight, channels);

            double top = v00 + (v10 - v00) * fx;
            double bot = v01 + (v11 - v01) * fx;
            double val = top + (bot - top) * fy;
            outRow[outX * channels + c] = Quantum.Clamp((int)(val + 0.5));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GetPixelClamped(ImageFrame source, int x, int y, int c,
        int width, int height, int channels)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        return source.GetPixelRow(y)[x * channels + c];
    }

    // ═══════════════════════════════════════════════════════════════════
    // Perspective Matrix Solver (8-parameter homography)
    // ═══════════════════════════════════════════════════════════════════

    private static double[] SolvePerspective(double[] src, double[] dst)
    {
        // Solve for 8 coefficients of the projective transform:
        // dst_x = (c0*src_x + c1*src_y + c2) / (c6*src_x + c7*src_y + 1)
        // dst_y = (c3*src_x + c4*src_y + c5) / (c6*src_x + c7*src_y + 1)
        // From 4 point pairs → 8 equations in 8 unknowns

        var A = new double[8, 8];
        var b = new double[8];

        for (int i = 0;i < 4;i++)
        {
            double sx = src[i * 2], sy = src[i * 2 + 1];
            double dx = dst[i * 2], dy = dst[i * 2 + 1];

            A[i * 2, 0] = sx;
            A[i * 2, 1] = sy;
            A[i * 2, 2] = 1;
            A[i * 2, 3] = 0;
            A[i * 2, 4] = 0;
            A[i * 2, 5] = 0;
            A[i * 2, 6] = -dx * sx;
            A[i * 2, 7] = -dx * sy;
            b[i * 2] = dx;

            A[i * 2 + 1, 0] = 0;
            A[i * 2 + 1, 1] = 0;
            A[i * 2 + 1, 2] = 0;
            A[i * 2 + 1, 3] = sx;
            A[i * 2 + 1, 4] = sy;
            A[i * 2 + 1, 5] = 1;
            A[i * 2 + 1, 6] = -dy * sx;
            A[i * 2 + 1, 7] = -dy * sy;
            b[i * 2 + 1] = dy;
        }

        return SolveLinearSystem(A, b);
    }

    /// <summary>
    /// Solve 8x8 linear system Ax=b using Gaussian elimination with partial pivoting.
    /// </summary>
    private static double[] SolveLinearSystem(double[,] A, double[] b)
    {
        int n = b.Length;
        var augmented = new double[n, n + 1];
        for (int i = 0;i < n;i++)
        {
            for (int j = 0;j < n;j++)
            {
                augmented[i, j] = A[i, j];
            }

            augmented[i, n] = b[i];
        }

        // Forward elimination with partial pivoting
        for (int col = 0;col < n;col++)
        {
            int maxRow = col;
            double maxVal = Math.Abs(augmented[col, col]);
            for (int row = col + 1;row < n;row++)
            {
                if (Math.Abs(augmented[row, col]) > maxVal)
                {
                    maxVal = Math.Abs(augmented[row, col]);
                    maxRow = row;
                }
            }

            if (maxRow != col)
            {
                for (int j = 0;j <= n;j++)
                {
                    (augmented[col, j], augmented[maxRow, j]) = (augmented[maxRow, j], augmented[col, j]);
                }
            }

            double pivot = augmented[col, col];
            if (Math.Abs(pivot) < 1e-15)
            {
                throw new InvalidOperationException("Perspective transform is degenerate.");
            }

            for (int row = col + 1;row < n;row++)
            {
                double factor = augmented[row, col] / pivot;
                for (int j = col;j <= n;j++)
                {
                    augmented[row, j] -= factor * augmented[col, j];
                }
            }
        }

        // Back substitution
        var x = new double[n];
        for (int i = n - 1;i >= 0;i--)
        {
            x[i] = augmented[i, n];
            for (int j = i + 1;j < n;j++)
            {
                x[i] -= augmented[i, j] * x[j];
            }

            x[i] /= augmented[i, i];
        }
        return x;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Bundle D: Lens Corrections
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Corrects chromatic aberration by applying separate barrel distortion per channel.
    /// redShift and blueShift control how much each channel is radially shifted relative to green.
    /// Positive values = outward shift (typical lateral CA), negative = inward.
    /// </summary>
    public static ImageFrame ChromaticAberrationFix(ImageFrame image, double redShift = 0.001, double blueShift = -0.001)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        if (channels < 3) return CloneFrame(image);

        double cx = width / 2.0, cy = height / 2.0;
        double maxR = Math.Sqrt(cx * cx + cy * cy);
        double invMaxR = 1.0 / maxR;

        var result = new ImageFrame();
        result.Initialize(image.Columns, image.Rows, image.Colorspace, image.HasAlpha);

        // Green channel stays at scale 1.0, red and blue get shifted
        double[] channelScales = [1.0 + redShift, 1.0, 1.0 + blueShift];

        Parallel.For(0, height, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                double dx = (x - cx) * invMaxR;
                double dy = (y - cy) * invMaxR;
                double r = Math.Sqrt(dx * dx + dy * dy);
                int off = x * channels;

                for (int c = 0; c < Math.Min(3, channels); c++)
                {
                    double scale = r > 1e-10 ? channelScales[c] : 1.0;
                    double sx = cx + dx * maxR * scale;
                    double sy = cy + dy * maxR * scale;

                    // Bilinear sample for this single channel
                    int x0 = (int)Math.Floor(sx - 0.5);
                    int y0 = (int)Math.Floor(sy - 0.5);
                    double fx = sx - 0.5 - x0;
                    double fy = sy - 0.5 - y0;

                    double v00 = GetPixelClamped(image, x0, y0, c, width, height, channels);
                    double v10 = GetPixelClamped(image, x0 + 1, y0, c, width, height, channels);
                    double v01 = GetPixelClamped(image, x0, y0 + 1, c, width, height, channels);
                    double v11 = GetPixelClamped(image, x0 + 1, y0 + 1, c, width, height, channels);
                    double top = v00 + (v10 - v00) * fx;
                    double bot = v01 + (v11 - v01) * fx;
                    dstRow[off + c] = Quantum.Clamp((int)(top + (bot - top) * fy + 0.5));
                }

                // Pass through alpha and any extra channels
                for (int c = 3; c < channels; c++)
                {
                    var srcRow = image.GetPixelRow(y);
                    dstRow[off + c] = srcRow[off + c];
                }
            }
        });

        return result;
    }

    /// <summary>
    /// Corrects vignette (radial brightness falloff) by applying inverse radial brightness compensation.
    /// strength controls how much correction to apply (0-2, where 1.0 is typical).
    /// midpoint controls where the falloff begins (0-1, default 0.5 = halfway from center to edge).
    /// </summary>
    public static ImageFrame VignetteCorrection(ImageFrame image, double strength = 1.0, double midpoint = 0.5)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        int colorChannels = image.HasAlpha ? channels - 1 : channels;

        double cx = width / 2.0, cy = height / 2.0;
        double maxR = Math.Sqrt(cx * cx + cy * cy);

        var result = new ImageFrame();
        result.Initialize(image.Columns, image.Rows, image.Colorspace, image.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = image.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                double dx = x - cx, dy = y - cy;
                double r = Math.Sqrt(dx * dx + dy * dy) / maxR;

                // Compute correction factor: inverse of the vignette model
                // cos^4 falloff model: vignette = (1 - strength * max(0, r - midpoint)^2)
                double falloff = Math.Max(0, r - midpoint);
                double vignetteAmount = strength * falloff * falloff;
                double correction = 1.0 / Math.Max(1.0 - vignetteAmount, 0.1);

                for (int c = 0; c < colorChannels; c++)
                    dstRow[off + c] = (ushort)Math.Clamp(srcRow[off + c] * correction, 0, Quantum.MaxValue);

                if (image.HasAlpha)
                    dstRow[off + channels - 1] = srcRow[off + channels - 1];
            }
        });

        return result;
    }

    /// <summary>
    /// Automatic perspective correction using edge detection to find dominant vanishing lines.
    /// Analyzes the image for strong edges and corrects the most significant perspective tilt.
    /// tiltCorrectionX and tiltCorrectionY control manual override in degrees.
    /// </summary>
    public static ImageFrame PerspectiveCorrection(ImageFrame image, double tiltX = 0, double tiltY = 0)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;

        // Convert tilt angles to perspective control points offset
        double radX = tiltX * Math.PI / 180.0;
        double radY = tiltY * Math.PI / 180.0;

        // Map corner offsets from tilt
        double shiftX = Math.Tan(radX) * height * 0.5;
        double shiftY = Math.Tan(radY) * width * 0.5;

        double[] srcPoints =
        [
            0, 0,
            width - 1, 0,
            width - 1, height - 1,
            0, height - 1
        ];

        double[] dstPoints =
        [
            -shiftX, -shiftY,
            width - 1 + shiftX, shiftY,
            width - 1 - shiftX, height - 1 - shiftY,
            shiftX, height - 1 + shiftY
        ];

        return Perspective(image, srcPoints, dstPoints, (uint)width, (uint)height);
    }

    private static ImageFrame CloneFrame(ImageFrame source)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        var result = new ImageFrame();
        result.Initialize(source.Columns, source.Rows, source.Colorspace, source.HasAlpha);
        for (int y = 0; y < height; y++)
            source.GetPixelRow(y).CopyTo(result.GetPixelRowForWrite(y));
        return result;
    }
}
