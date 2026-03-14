// SharpImage — Image stitching and panorama operations.
// Harris corners, BRIEF descriptors, feature matching, RANSAC homography, multi-band blending.
// Bundle E of the feature roadmap.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Effects;

/// <summary>
/// Image stitching pipeline: corner detection, feature description, matching, homography estimation, and blending.
/// </summary>
public static class StitchingOps
{
    // ─── Data types ─────────────────────────────────────────────────

    /// <summary>
    /// A detected corner / keypoint with location and response strength.
    /// </summary>
    public readonly struct Keypoint
    {
        public readonly int X;
        public readonly int Y;
        public readonly double Response;

        public Keypoint(int x, int y, double response)
        {
            X = x; Y = y; Response = response;
        }
    }

    /// <summary>
    /// A feature with keypoint location and binary descriptor.
    /// </summary>
    public readonly struct Feature
    {
        public readonly Keypoint Point;
        public readonly ulong[] Descriptor; // 256-bit BRIEF descriptor stored in 4 ulongs

        public Feature(Keypoint point, ulong[] descriptor)
        {
            Point = point;
            Descriptor = descriptor;
        }
    }

    /// <summary>
    /// A match between two features.
    /// </summary>
    public readonly struct FeatureMatch
    {
        public readonly int IndexA;
        public readonly int IndexB;
        public readonly int Distance; // Hamming distance

        public FeatureMatch(int indexA, int indexB, int distance)
        {
            IndexA = indexA; IndexB = indexB; Distance = distance;
        }
    }

    // ─── Harris Corner Detection ────────────────────────────────────

    /// <summary>
    /// Detects Harris corners in the image. Returns keypoints sorted by response strength.
    /// blockSize controls the neighborhood for structure tensor, k is the Harris sensitivity (0.04-0.06).
    /// </summary>
    public static Keypoint[] DetectHarrisCorners(ImageFrame source, int maxCorners = 500,
        double qualityLevel = 0.01, int blockSize = 3, double k = 0.04)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Convert to grayscale luminance
        int pixelCount = height * width;
        var gray = ArrayPool<double>.Shared.Rent(pixelCount);
        var gradX = ArrayPool<double>.Shared.Rent(pixelCount);
        var gradY = ArrayPool<double>.Shared.Rent(pixelCount);
        var response = ArrayPool<double>.Shared.Rent(pixelCount);
        Array.Clear(gradX, 0, pixelCount);
        Array.Clear(gradY, 0, pixelCount);
        Array.Clear(response, 0, pixelCount);

        try
        {
            for (int y = 0; y < height; y++)
            {
                var row = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int off = x * channels;
                    if (channels >= 3)
                        gray[y * width + x] = (row[off] * 0.2126 + row[off + 1] * 0.7152 + row[off + 2] * 0.0722) * Quantum.Scale;
                    else
                        gray[y * width + x] = row[off] * Quantum.Scale;
                }
            }

            // Compute gradients using Sobel
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    gradX[y * width + x] =
                        -gray[(y - 1) * width + (x - 1)] + gray[(y - 1) * width + (x + 1)]
                        - 2 * gray[y * width + (x - 1)] + 2 * gray[y * width + (x + 1)]
                        - gray[(y + 1) * width + (x - 1)] + gray[(y + 1) * width + (x + 1)];

                    gradY[y * width + x] =
                        -gray[(y - 1) * width + (x - 1)] - 2 * gray[(y - 1) * width + x] - gray[(y - 1) * width + (x + 1)]
                        + gray[(y + 1) * width + (x - 1)] + 2 * gray[(y + 1) * width + x] + gray[(y + 1) * width + (x + 1)];
                }
            }

            // Compute Harris response
            int half = blockSize / 2;
            double maxResponse = 0;

            for (int y = half + 1; y < height - half - 1; y++)
            {
                for (int x = half + 1; x < width - half - 1; x++)
                {
                    double sumIx2 = 0, sumIy2 = 0, sumIxIy = 0;
                    for (int dy = -half; dy <= half; dy++)
                    {
                        for (int dx = -half; dx <= half; dx++)
                        {
                            int idx = (y + dy) * width + (x + dx);
                            double ix = gradX[idx], iy = gradY[idx];
                            sumIx2 += ix * ix;
                            sumIy2 += iy * iy;
                            sumIxIy += ix * iy;
                        }
                    }

                    double det = sumIx2 * sumIy2 - sumIxIy * sumIxIy;
                    double trace = sumIx2 + sumIy2;
                    double r = det - k * trace * trace;
                    response[y * width + x] = r;
                    if (r > maxResponse) maxResponse = r;
                }
            }

            // Non-maximum suppression and threshold
            double threshold = maxResponse * qualityLevel;
            var corners = new List<Keypoint>();
            int nmsRadius = Math.Max(3, blockSize);

            for (int y = nmsRadius; y < height - nmsRadius; y++)
            {
                for (int x = nmsRadius; x < width - nmsRadius; x++)
                {
                    double r = response[y * width + x];
                    if (r <= threshold) continue;

                    // Check if local maximum
                    bool isMax = true;
                    for (int dy = -nmsRadius; dy <= nmsRadius && isMax; dy++)
                        for (int dx = -nmsRadius; dx <= nmsRadius && isMax; dx++)
                            if ((dx != 0 || dy != 0) && response[(y + dy) * width + (x + dx)] >= r)
                                isMax = false;

                    if (isMax)
                        corners.Add(new Keypoint(x, y, r));
                }
            }

            // Sort by response and take top N
            corners.Sort((a, b) => b.Response.CompareTo(a.Response));
            if (corners.Count > maxCorners)
                corners.RemoveRange(maxCorners, corners.Count - maxCorners);

            return corners.ToArray();
        }
        finally
        {
            ArrayPool<double>.Shared.Return(gray);
            ArrayPool<double>.Shared.Return(gradX);
            ArrayPool<double>.Shared.Return(gradY);
            ArrayPool<double>.Shared.Return(response);
        }
    }

    // ─── BRIEF Feature Descriptors ──────────────────────────────────

    /// <summary>
    /// Computes 256-bit BRIEF binary descriptors for keypoints.
    /// Requires a smoothed grayscale version of the image.
    /// </summary>
    public static Feature[] ComputeBriefDescriptors(ImageFrame source, Keypoint[] keypoints, int patchSize = 31)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        int halfPatch = patchSize / 2;

        // Build smoothed grayscale
        int pixelCount = height * width;
        var gray = ArrayPool<double>.Shared.Rent(pixelCount);
        var smoothed = ArrayPool<double>.Shared.Rent(pixelCount);
        Array.Clear(smoothed, 0, pixelCount);

        try
        {
            for (int y = 0; y < height; y++)
            {
                var row = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int off = x * channels;
                    if (channels >= 3)
                        gray[y * width + x] = row[off] * 0.2126 + row[off + 1] * 0.7152 + row[off + 2] * 0.0722;
                    else
                        gray[y * width + x] = row[off];
                }
            }

            // Simple box blur for smoothing
            int blurR = 2;
            for (int y = blurR; y < height - blurR; y++)
            {
                for (int x = blurR; x < width - blurR; x++)
                {
                    double sum = 0;
                    for (int dy = -blurR; dy <= blurR; dy++)
                        for (int dx = -blurR; dx <= blurR; dx++)
                            sum += gray[(y + dy) * width + (x + dx)];
                    smoothed[y * width + x] = sum / ((2 * blurR + 1) * (2 * blurR + 1));
                }
            }

            // Generate deterministic test pairs (256 pairs for 256-bit descriptor)
            var pairs = GenerateBriefPairs(patchSize);

            var features = new Feature[keypoints.Length];
            for (int i = 0; i < keypoints.Length; i++)
            {
                var kp = keypoints[i];
                if (kp.X < halfPatch || kp.X >= width - halfPatch || kp.Y < halfPatch || kp.Y >= height - halfPatch)
                {
                    features[i] = new Feature(kp, new ulong[4]);
                    continue;
                }

                var descriptor = new ulong[4];
                for (int bit = 0; bit < 256; bit++)
                {
                    int ax = kp.X + pairs[bit * 4];
                    int ay = kp.Y + pairs[bit * 4 + 1];
                    int bx = kp.X + pairs[bit * 4 + 2];
                    int by = kp.Y + pairs[bit * 4 + 3];

                    ax = Math.Clamp(ax, 0, width - 1);
                    ay = Math.Clamp(ay, 0, height - 1);
                    bx = Math.Clamp(bx, 0, width - 1);
                    by = Math.Clamp(by, 0, height - 1);

                    if (smoothed[ay * width + ax] < smoothed[by * width + bx])
                        descriptor[bit / 64] |= 1UL << (bit % 64);
                }

                features[i] = new Feature(kp, descriptor);
            }

            return features;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(gray);
            ArrayPool<double>.Shared.Return(smoothed);
        }
    }

    // ─── Feature Matching ───────────────────────────────────────────

    /// <summary>
    /// Matches features between two sets using Hamming distance on BRIEF descriptors.
    /// Uses Lowe's ratio test to reject ambiguous matches.
    /// </summary>
    public static FeatureMatch[] MatchFeatures(Feature[] featuresA, Feature[] featuresB, double ratioThreshold = 0.75)
    {
        var matches = new List<FeatureMatch>();

        for (int i = 0; i < featuresA.Length; i++)
        {
            int bestDist = int.MaxValue;
            int secondBestDist = int.MaxValue;
            int bestIdx = -1;

            for (int j = 0; j < featuresB.Length; j++)
            {
                int dist = HammingDistance(featuresA[i].Descriptor, featuresB[j].Descriptor);
                if (dist < bestDist)
                {
                    secondBestDist = bestDist;
                    bestDist = dist;
                    bestIdx = j;
                }
                else if (dist < secondBestDist)
                {
                    secondBestDist = dist;
                }
            }

            // Lowe's ratio test
            if (bestIdx >= 0 && secondBestDist > 0 && (double)bestDist / secondBestDist < ratioThreshold)
                matches.Add(new FeatureMatch(i, bestIdx, bestDist));
        }

        return matches.ToArray();
    }

    // ─── RANSAC Homography ──────────────────────────────────────────

    /// <summary>
    /// Estimates a 3×3 homography matrix from matched features using RANSAC.
    /// Returns the 8 perspective coefficients compatible with Distort.Perspective.
    /// </summary>
    public static double[] EstimateHomographyRANSAC(Feature[] featuresA, Feature[] featuresB,
        FeatureMatch[] matches, int iterations = 1000, double inlierThreshold = 3.0)
    {
        if (matches.Length < 4)
            return [1, 0, 0, 0, 1, 0, 0, 0]; // identity

        var rng = new Random(42); // deterministic for reproducibility
        double[] bestHomography = [1, 0, 0, 0, 1, 0, 0, 0];
        int bestInlierCount = 0;

        // Pre-allocate reusable buffers for the RANSAC loop
        var sample = new int[4];
        var srcPts = new double[8];
        var dstPts = new double[8];

        for (int iter = 0; iter < iterations; iter++)
        {
            // Pick 4 random matches
            for (int i = 0; i < 4; i++)
            {
                int idx;
                bool duplicate;
                do
                {
                    idx = rng.Next(matches.Length);
                    duplicate = false;
                    for (int j = 0; j < i; j++)
                        if (sample[j] == idx) { duplicate = true; break; }
                } while (duplicate);
                sample[i] = idx;
            }

            // Build point correspondences
            for (int i = 0; i < 4; i++)
            {
                var m = matches[sample[i]];
                srcPts[i * 2] = featuresA[m.IndexA].Point.X;
                srcPts[i * 2 + 1] = featuresA[m.IndexA].Point.Y;
                dstPts[i * 2] = featuresB[m.IndexB].Point.X;
                dstPts[i * 2 + 1] = featuresB[m.IndexB].Point.Y;
            }

            // Solve homography
            double[] h;
            try { h = SolveHomography(srcPts, dstPts); }
            catch { continue; }

            // Count inliers
            int inlierCount = 0;
            foreach (var m in matches)
            {
                double sx = featuresA[m.IndexA].Point.X;
                double sy = featuresA[m.IndexA].Point.Y;
                double dx = featuresB[m.IndexB].Point.X;
                double dy = featuresB[m.IndexB].Point.Y;

                double w = h[6] * sx + h[7] * sy + 1.0;
                if (Math.Abs(w) < 1e-10) continue;
                double px = (h[0] * sx + h[1] * sy + h[2]) / w;
                double py = (h[3] * sx + h[4] * sy + h[5]) / w;

                double err = Math.Sqrt((px - dx) * (px - dx) + (py - dy) * (py - dy));
                if (err < inlierThreshold) inlierCount++;
            }

            if (inlierCount > bestInlierCount)
            {
                bestInlierCount = inlierCount;
                bestHomography = h;
            }
        }

        return bestHomography;
    }

    // ─── Multi-Band Blending ────────────────────────────────────────

    /// <summary>
    /// Blends two images using Laplacian pyramid multi-band blending.
    /// The images should be the same size (use StitchPanorama for full pipeline).
    /// blendMask controls the weight: white=imageA, black=imageB.
    /// </summary>
    public static ImageFrame LaplacianBlend(ImageFrame imageA, ImageFrame imageB,
        ImageFrame blendMask, int pyramidLevels = 4)
    {
        int width = Math.Min((int)imageA.Columns, (int)imageB.Columns);
        int height = Math.Min((int)imageA.Rows, (int)imageB.Rows);
        int channels = imageA.NumberOfChannels;

        // Convert frames to double arrays for pyramid processing
        var dataA = FrameToDoubles(imageA, width, height, channels);
        var dataB = FrameToDoubles(imageB, width, height, channels);
        var maskData = FrameToDoubles(blendMask, width, height, 1);

        // Build Gaussian pyramids for the mask
        var maskPyramid = BuildGaussianPyramid(maskData, width, height, 1, pyramidLevels);

        // Build Laplacian pyramids for both images
        var lapA = BuildLaplacianPyramid(dataA, width, height, channels, pyramidLevels);
        var lapB = BuildLaplacianPyramid(dataB, width, height, channels, pyramidLevels);

        // Blend Laplacian pyramids using mask pyramid
        var blendedPyramid = new List<(double[] data, int w, int h)>();
        for (int level = 0; level < lapA.Count; level++)
        {
            var (aData, w, h) = lapA[level];
            var (bData, _, _) = lapB[level];
            var (mData, _, _) = maskPyramid[level];

            var blended = new double[w * h * channels];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double alpha = mData[y * w + x];
                    for (int c = 0; c < channels; c++)
                    {
                        int idx = (y * w + x) * channels + c;
                        blended[idx] = alpha * aData[idx] + (1.0 - alpha) * bData[idx];
                    }
                }
            }
            blendedPyramid.Add((blended, w, h));
        }

        // Reconstruct from blended Laplacian pyramid
        var reconstructed = ReconstructFromLaplacian(blendedPyramid, channels);

        // Convert back to ImageFrame
        var result = new ImageFrame();
        result.Initialize(width, height, imageA.Colorspace, imageA.HasAlpha);
        DoublesToFrame(reconstructed, result, width, height, channels);
        return result;
    }

    /// <summary>
    /// Full panorama stitching: detects features, matches, estimates homography,
    /// warps the second image, and blends using Laplacian pyramid.
    /// </summary>
    public static ImageFrame StitchPanorama(ImageFrame imageA, ImageFrame imageB,
        int maxCorners = 500, double matchRatio = 0.75, int pyramidLevels = 4)
    {
        // 1. Detect corners
        var cornersA = DetectHarrisCorners(imageA, maxCorners);
        var cornersB = DetectHarrisCorners(imageB, maxCorners);

        if (cornersA.Length < 4 || cornersB.Length < 4)
            return imageA; // Not enough features, return first image

        // 2. Compute descriptors
        var featuresA = ComputeBriefDescriptors(imageA, cornersA);
        var featuresB = ComputeBriefDescriptors(imageB, cornersB);

        // 3. Match features
        var matches = MatchFeatures(featuresA, featuresB, matchRatio);
        if (matches.Length < 4)
            return imageA;

        // 4. RANSAC homography
        var homography = EstimateHomographyRANSAC(featuresA, featuresB, matches);

        // 5. Compute output bounds
        int widthA = (int)imageA.Columns, heightA = (int)imageA.Rows;
        int widthB = (int)imageB.Columns, heightB = (int)imageB.Rows;

        // Transform corners of imageB to find bounds
        var cornersOfB = new (double x, double y)[]
        {
            TransformPoint(0, 0, homography),
            TransformPoint(widthB - 1, 0, homography),
            TransformPoint(widthB - 1, heightB - 1, homography),
            TransformPoint(0, heightB - 1, homography)
        };

        double minX = Math.Min(0, cornersOfB.Min(c => c.x));
        double maxX = Math.Max(widthA - 1, cornersOfB.Max(c => c.x));
        double minY = Math.Min(0, cornersOfB.Min(c => c.y));
        double maxY = Math.Max(heightA - 1, cornersOfB.Max(c => c.y));

        int outWidth = (int)Math.Ceiling(maxX - minX) + 1;
        int outHeight = (int)Math.Ceiling(maxY - minY) + 1;

        // Limit output to reasonable size
        outWidth = Math.Min(outWidth, widthA + widthB);
        outHeight = Math.Min(outHeight, Math.Max(heightA, heightB) * 2);

        int offsetX = (int)Math.Floor(-minX);
        int offsetY = (int)Math.Floor(-minY);

        // 6. Create canvas with both images
        int channels = imageA.NumberOfChannels;
        var canvasA = new ImageFrame();
        canvasA.Initialize(outWidth, outHeight, imageA.Colorspace, imageA.HasAlpha);
        var canvasB = new ImageFrame();
        canvasB.Initialize(outWidth, outHeight, imageA.Colorspace, imageA.HasAlpha);
        var maskFrame = new ImageFrame();
        maskFrame.Initialize(outWidth, outHeight, ColorspaceType.Gray, false);

        // Place imageA at offset
        for (int y = 0; y < heightA; y++)
        {
            var srcRow = imageA.GetPixelRow(y);
            int dy = y + offsetY;
            if (dy < 0 || dy >= outHeight) continue;
            var dstRow = canvasA.GetPixelRowForWrite(dy);
            for (int x = 0; x < widthA; x++)
            {
                int dx = x + offsetX;
                if (dx < 0 || dx >= outWidth) continue;
                for (int c = 0; c < channels; c++)
                    dstRow[dx * channels + c] = srcRow[x * channels + c];
            }
        }

        // Warp imageB using inverse homography
        var invH = InvertHomography(homography);
        for (int y = 0; y < outHeight; y++)
        {
            var dstRow = canvasB.GetPixelRowForWrite(y);
            var maskRow = maskFrame.GetPixelRowForWrite(y);
            for (int x = 0; x < outWidth; x++)
            {
                double sx = x - offsetX;
                double sy = y - offsetY;
                var (bx, by) = TransformPointInverse(sx, sy, invH);

                if (bx >= 0 && bx < widthB - 1 && by >= 0 && by < heightB - 1)
                {
                    // Bilinear sample from imageB
                    int x0 = (int)Math.Floor(bx), y0 = (int)Math.Floor(by);
                    double fx = bx - x0, fy = by - y0;
                    for (int c = 0; c < channels; c++)
                    {
                        double v00 = imageB.GetPixelRow(y0)[x0 * channels + c];
                        double v10 = imageB.GetPixelRow(y0)[(x0 + 1) * channels + c];
                        double v01 = imageB.GetPixelRow(y0 + 1)[x0 * channels + c];
                        double v11 = imageB.GetPixelRow(y0 + 1)[(x0 + 1) * channels + c];
                        double top = v00 + (v10 - v00) * fx;
                        double bot = v01 + (v11 - v01) * fx;
                        dstRow[x * channels + c] = (ushort)Math.Clamp(top + (bot - top) * fy, 0, Quantum.MaxValue);
                    }
                }

                // Build blend mask: where imageA is present → white; where only imageB → black
                bool hasA = (x - offsetX) >= 0 && (x - offsetX) < widthA && (y - offsetY) >= 0 && (y - offsetY) < heightA;
                bool hasB = bx >= 0 && bx < widthB && by >= 0 && by < heightB;
                if (hasA && hasB)
                    maskRow[x] = (ushort)(Quantum.MaxValue / 2); // overlap region
                else if (hasA)
                    maskRow[x] = Quantum.MaxValue;
                else
                    maskRow[x] = 0;
            }
        }

        // 7. Blend using Laplacian pyramid
        return LaplacianBlend(canvasA, canvasB, maskFrame, pyramidLevels);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Internal helpers
    // ═══════════════════════════════════════════════════════════════════

    private static int[] GenerateBriefPairs(int patchSize)
    {
        // Deterministic pseudo-random pairs using a fixed seed
        int half = patchSize / 2;
        var rng = new Random(12345);
        var pairs = new int[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            pairs[i * 4] = rng.Next(-half, half + 1);
            pairs[i * 4 + 1] = rng.Next(-half, half + 1);
            pairs[i * 4 + 2] = rng.Next(-half, half + 1);
            pairs[i * 4 + 3] = rng.Next(-half, half + 1);
        }
        return pairs;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HammingDistance(ulong[] a, ulong[] b)
    {
        int dist = 0;
        for (int i = 0; i < 4; i++)
            dist += System.Numerics.BitOperations.PopCount(a[i] ^ b[i]);
        return dist;
    }

    private static double[] SolveHomography(double[] srcPts, double[] dstPts)
    {
        // Solve for H: dst = H * src (3×3 homography, 8 DOF)
        // Same 8-parameter system as perspective transform
        var A = new double[8, 8];
        var b = new double[8];

        for (int i = 0; i < 4; i++)
        {
            double sx = srcPts[i * 2], sy = srcPts[i * 2 + 1];
            double dx = dstPts[i * 2], dy = dstPts[i * 2 + 1];

            A[i * 2, 0] = sx; A[i * 2, 1] = sy; A[i * 2, 2] = 1;
            A[i * 2, 6] = -dx * sx; A[i * 2, 7] = -dx * sy;
            b[i * 2] = dx;

            A[i * 2 + 1, 3] = sx; A[i * 2 + 1, 4] = sy; A[i * 2 + 1, 5] = 1;
            A[i * 2 + 1, 6] = -dy * sx; A[i * 2 + 1, 7] = -dy * sy;
            b[i * 2 + 1] = dy;
        }

        return SolveLinear8x8(A, b);
    }

    private static double[] SolveLinear8x8(double[,] A, double[] b)
    {
        int n = 8;
        var aug = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) aug[i, j] = A[i, j];
            aug[i, n] = b[i];
        }

        for (int col = 0; col < n; col++)
        {
            int maxRow = col;
            double maxVal = Math.Abs(aug[col, col]);
            for (int row = col + 1; row < n; row++)
            {
                double v = Math.Abs(aug[row, col]);
                if (v > maxVal) { maxVal = v; maxRow = row; }
            }
            if (maxVal < 1e-12) throw new InvalidOperationException("Degenerate homography.");
            if (maxRow != col)
                for (int j = 0; j <= n; j++) (aug[col, j], aug[maxRow, j]) = (aug[maxRow, j], aug[col, j]);

            for (int row = col + 1; row < n; row++)
            {
                double factor = aug[row, col] / aug[col, col];
                for (int j = col; j <= n; j++) aug[row, j] -= factor * aug[col, j];
            }
        }

        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            x[i] = aug[i, n];
            for (int j = i + 1; j < n; j++) x[i] -= aug[i, j] * x[j];
            x[i] /= aug[i, i];
        }
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double x, double y) TransformPoint(double x, double y, double[] h)
    {
        double w = h[6] * x + h[7] * y + 1.0;
        if (Math.Abs(w) < 1e-10) return (x, y);
        return ((h[0] * x + h[1] * y + h[2]) / w, (h[3] * x + h[4] * y + h[5]) / w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double x, double y) TransformPointInverse(double x, double y, double[] h)
    {
        return TransformPoint(x, y, h);
    }

    private static double[] InvertHomography(double[] h)
    {
        // Invert the 3×3 homography matrix [h0 h1 h2; h3 h4 h5; h6 h7 1]
        double[,] H =
        {
            { h[0], h[1], h[2] },
            { h[3], h[4], h[5] },
            { h[6], h[7], 1.0 }
        };

        double det = H[0, 0] * (H[1, 1] * H[2, 2] - H[1, 2] * H[2, 1])
                   - H[0, 1] * (H[1, 0] * H[2, 2] - H[1, 2] * H[2, 0])
                   + H[0, 2] * (H[1, 0] * H[2, 1] - H[1, 1] * H[2, 0]);

        if (Math.Abs(det) < 1e-12) return [1, 0, 0, 0, 1, 0, 0, 0]; // identity fallback

        double invDet = 1.0 / det;
        double[,] inv = new double[3, 3];
        inv[0, 0] = (H[1, 1] * H[2, 2] - H[1, 2] * H[2, 1]) * invDet;
        inv[0, 1] = (H[0, 2] * H[2, 1] - H[0, 1] * H[2, 2]) * invDet;
        inv[0, 2] = (H[0, 1] * H[1, 2] - H[0, 2] * H[1, 1]) * invDet;
        inv[1, 0] = (H[1, 2] * H[2, 0] - H[1, 0] * H[2, 2]) * invDet;
        inv[1, 1] = (H[0, 0] * H[2, 2] - H[0, 2] * H[2, 0]) * invDet;
        inv[1, 2] = (H[0, 2] * H[1, 0] - H[0, 0] * H[1, 2]) * invDet;
        inv[2, 0] = (H[1, 0] * H[2, 1] - H[1, 1] * H[2, 0]) * invDet;
        inv[2, 1] = (H[0, 1] * H[2, 0] - H[0, 0] * H[2, 1]) * invDet;
        inv[2, 2] = (H[0, 0] * H[1, 1] - H[0, 1] * H[1, 0]) * invDet;

        // Normalize so inv[2,2] = 1
        double scale = inv[2, 2];
        if (Math.Abs(scale) < 1e-12) return [1, 0, 0, 0, 1, 0, 0, 0];
        return [inv[0, 0] / scale, inv[0, 1] / scale, inv[0, 2] / scale,
                inv[1, 0] / scale, inv[1, 1] / scale, inv[1, 2] / scale,
                inv[2, 0] / scale, inv[2, 1] / scale];
    }

    // ─── Pyramid helpers ────────────────────────────────────────────

    private static double[] FrameToDoubles(ImageFrame frame, int width, int height, int useChannels)
    {
        int frameCh = frame.NumberOfChannels;
        var data = new double[width * height * useChannels];
        for (int y = 0; y < Math.Min(height, (int)frame.Rows); y++)
        {
            var row = frame.GetPixelRow(y);
            for (int x = 0; x < Math.Min(width, (int)frame.Columns); x++)
            {
                for (int c = 0; c < useChannels; c++)
                {
                    int srcC = Math.Min(c, frameCh - 1);
                    data[(y * width + x) * useChannels + c] = row[x * frameCh + srcC] * Quantum.Scale;
                }
            }
        }
        return data;
    }

    private static void DoublesToFrame(double[] data, ImageFrame frame, int width, int height, int channels)
    {
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    double val = data[(y * width + x) * channels + c];
                    row[x * channels + c] = (ushort)Math.Clamp(val * Quantum.MaxValue, 0, Quantum.MaxValue);
                }
            }
        }
    }

    private static double[] DownsampleHalf(double[] data, int width, int height, int channels)
    {
        int newW = width / 2, newH = height / 2;
        if (newW < 1) newW = 1;
        if (newH < 1) newH = 1;
        var result = new double[newW * newH * channels];

        for (int y = 0; y < newH; y++)
        {
            for (int x = 0; x < newW; x++)
            {
                int sx = x * 2, sy = y * 2;
                for (int c = 0; c < channels; c++)
                {
                    double sum = data[(sy * width + sx) * channels + c];
                    if (sx + 1 < width) sum += data[(sy * width + sx + 1) * channels + c];
                    if (sy + 1 < height) sum += data[((sy + 1) * width + sx) * channels + c];
                    if (sx + 1 < width && sy + 1 < height) sum += data[((sy + 1) * width + sx + 1) * channels + c];
                    result[(y * newW + x) * channels + c] = sum / 4.0;
                }
            }
        }
        return result;
    }

    private static double[] UpsampleDouble(double[] data, int width, int height, int channels, int targetW, int targetH)
    {
        var result = new double[targetW * targetH * channels];
        for (int y = 0; y < targetH; y++)
        {
            double sy = y * height / (double)targetH;
            int y0 = Math.Min((int)sy, height - 1);
            int y1 = Math.Min(y0 + 1, height - 1);
            double fy = sy - y0;

            for (int x = 0; x < targetW; x++)
            {
                double sx = x * width / (double)targetW;
                int x0 = Math.Min((int)sx, width - 1);
                int x1 = Math.Min(x0 + 1, width - 1);
                double fx = sx - x0;

                for (int c = 0; c < channels; c++)
                {
                    double v00 = data[(y0 * width + x0) * channels + c];
                    double v10 = data[(y0 * width + x1) * channels + c];
                    double v01 = data[(y1 * width + x0) * channels + c];
                    double v11 = data[(y1 * width + x1) * channels + c];
                    double top = v00 + (v10 - v00) * fx;
                    double bot = v01 + (v11 - v01) * fx;
                    result[(y * targetW + x) * channels + c] = top + (bot - top) * fy;
                }
            }
        }
        return result;
    }

    private static List<(double[] data, int w, int h)> BuildGaussianPyramid(
        double[] data, int width, int height, int channels, int levels)
    {
        var pyramid = new List<(double[] data, int w, int h)> { (data, width, height) };
        var current = data;
        int w = width, h = height;
        for (int i = 1; i < levels; i++)
        {
            current = DownsampleHalf(current, w, h, channels);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            pyramid.Add((current, w, h));
        }
        return pyramid;
    }

    private static List<(double[] data, int w, int h)> BuildLaplacianPyramid(
        double[] data, int width, int height, int channels, int levels)
    {
        var gaussian = BuildGaussianPyramid(data, width, height, channels, levels);
        var laplacian = new List<(double[] data, int w, int h)>();

        for (int i = 0; i < gaussian.Count - 1; i++)
        {
            var (gData, gw, gh) = gaussian[i];
            var (nextData, nw, nh) = gaussian[i + 1];
            var upsampled = UpsampleDouble(nextData, nw, nh, channels, gw, gh);

            var diff = new double[gw * gh * channels];
            for (int j = 0; j < diff.Length; j++)
                diff[j] = gData[j] - upsampled[j];

            laplacian.Add((diff, gw, gh));
        }

        // Last level is the lowest-resolution Gaussian
        var (lastData, lw, lh) = gaussian[^1];
        laplacian.Add((lastData, lw, lh));

        return laplacian;
    }

    private static double[] ReconstructFromLaplacian(List<(double[] data, int w, int h)> laplacian, int channels)
    {
        var (current, cw, ch) = laplacian[^1];

        for (int i = laplacian.Count - 2; i >= 0; i--)
        {
            var (lapData, lw, lh) = laplacian[i];
            var upsampled = UpsampleDouble(current, cw, ch, channels, lw, lh);

            current = new double[lw * lh * channels];
            for (int j = 0; j < current.Length; j++)
                current[j] = upsampled[j] + lapData[j];

            cw = lw; ch = lh;
        }

        return current;
    }
}
