using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Analysis;

/// <summary>
/// DCT-based perceptual image hashing for similarity detection. Generates a 64-bit fingerprint
/// that remains stable under minor edits (resize, compression, color shifts).
/// </summary>
public static class PerceptualHash
{
    /// <summary>
    /// Compute a perceptual hash (pHash) for the image. The image is resized to 32×32 grayscale,
    /// a DCT is applied, and the top-left 8×8 low-frequency coefficients (excluding DC) are
    /// thresholded against their median to produce a 64-bit hash.
    /// </summary>
    public static ulong Compute(ImageFrame image)
    {
        const int hashSize = 8;
        const int dctSize = 32;

        // Step 1: Convert to 32×32 grayscale
        double[] gray = ResizeToGrayscale(image, dctSize);

        // Step 2: 2D DCT on 32×32 block
        double[] dct = new double[dctSize * dctSize];
        ApplyDct2D(gray, dct, dctSize);

        // Step 3: Extract top-left 8×8 (low-frequency), skip DC at (0,0)
        double[] lowFreq = new double[hashSize * hashSize - 1];
        int idx = 0;
        for (int y = 0; y < hashSize; y++)
        {
            for (int x = 0; x < hashSize; x++)
            {
                if (x == 0 && y == 0) continue;
                lowFreq[idx++] = dct[y * dctSize + x];
            }
        }

        // Step 4: Compute median
        double[] sorted = new double[lowFreq.Length];
        Array.Copy(lowFreq, sorted, lowFreq.Length);
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];

        // Step 5: Generate hash — bit is 1 if coefficient >= median
        ulong hash = 0;
        for (int i = 0; i < 64; i++)
        {
            if (i < lowFreq.Length && lowFreq[i] >= median)
            {
                hash |= 1UL << i;
            }
        }

        return hash;
    }

    /// <summary>
    /// Compute the Hamming distance between two perceptual hashes. Lower = more similar.
    /// A distance of 0 means identical hashes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HammingDistance(ulong hash1, ulong hash2)
    {
        return System.Numerics.BitOperations.PopCount(hash1 ^ hash2);
    }

    /// <summary>
    /// Check if two hashes are similar within a given threshold (default: 10 bits out of 64).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreSimilar(ulong hash1, ulong hash2, int threshold = 10)
    {
        return HammingDistance(hash1, hash2) <= threshold;
    }

    /// <summary>
    /// Format a hash as a 16-character hex string.
    /// </summary>
    public static string ToHexString(ulong hash) => hash.ToString("x16");

    /// <summary>
    /// Parse a 16-character hex string back to a hash.
    /// </summary>
    public static ulong FromHexString(string hex) => ulong.Parse(hex, System.Globalization.NumberStyles.HexNumber);

    // ─── Average Hash (aHash) ──────────────────────────────────────

    /// <summary>
    /// Compute an average hash (aHash). The image is resized to 8×8 grayscale,
    /// and each pixel is compared to the mean intensity to produce a 64-bit hash.
    /// Fastest hash, good for exact/near-exact duplicate detection.
    /// </summary>
    public static ulong ComputeAverageHash(ImageFrame image)
    {
        const int size = 8;
        double[] gray = ResizeToGrayscale(image, size);

        // Compute mean
        double mean = 0;
        for (int i = 0; i < gray.Length; i++)
            mean += gray[i];
        mean /= gray.Length;

        // Generate hash — bit is 1 if pixel >= mean
        ulong hash = 0;
        for (int i = 0; i < 64; i++)
        {
            if (gray[i] >= mean)
                hash |= 1UL << i;
        }

        return hash;
    }

    // ─── Difference Hash (dHash) ───────────────────────────────────

    /// <summary>
    /// Compute a difference hash (dHash). The image is resized to 9×8 grayscale,
    /// and each pixel is compared to its right neighbor. Encodes gradient direction,
    /// making it more robust than aHash against brightness/contrast changes.
    /// </summary>
    public static ulong ComputeDifferenceHash(ImageFrame image)
    {
        const int width = 9;
        const int height = 8;
        double[] gray = ResizeToGrayscale(image, width, height);

        // Generate hash — bit is 1 if pixel[x] > pixel[x+1]
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width - 1; x++)
            {
                if (gray[y * width + x] > gray[y * width + x + 1])
                    hash |= 1UL << bit;
                bit++;
            }
        }

        return hash;
    }

    // ─── Wavelet Hash (wHash) ──────────────────────────────────────

    /// <summary>
    /// Compute a wavelet hash (wHash). The image is resized to 8×8 grayscale,
    /// a Haar wavelet transform is applied, and the low-frequency coefficients
    /// are thresholded against their median. More resilient to geometric transforms
    /// than aHash while being simpler than pHash.
    /// </summary>
    public static ulong ComputeWaveletHash(ImageFrame image)
    {
        const int size = 8;
        double[] gray = ResizeToGrayscale(image, size);

        // Apply 2D Haar wavelet transform (one level)
        double[] transformed = new double[size * size];
        Array.Copy(gray, transformed, gray.Length);
        HaarWavelet2D(transformed, size);

        // Compute median of all coefficients
        double[] sorted = new double[transformed.Length];
        Array.Copy(transformed, sorted, transformed.Length);
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];

        // Generate hash
        ulong hash = 0;
        for (int i = 0; i < 64; i++)
        {
            if (transformed[i] >= median)
                hash |= 1UL << i;
        }

        return hash;
    }

    /// <summary>
    /// Compute all four hash types for an image, returning a comprehensive fingerprint.
    /// </summary>
    public static ImageHashSet ComputeAll(ImageFrame image)
    {
        return new ImageHashSet
        {
            AverageHash = ComputeAverageHash(image),
            DifferenceHash = ComputeDifferenceHash(image),
            PerceptualHash = Compute(image),
            WaveletHash = ComputeWaveletHash(image),
        };
    }

    /// <summary>
    /// Compare two image hash sets and return similarity scores (0 = identical, 64 = completely different).
    /// </summary>
    public static HashComparisonResult Compare(ImageHashSet a, ImageHashSet b)
    {
        return new HashComparisonResult
        {
            AverageDistance = HammingDistance(a.AverageHash, b.AverageHash),
            DifferenceDistance = HammingDistance(a.DifferenceHash, b.DifferenceHash),
            PerceptualDistance = HammingDistance(a.PerceptualHash, b.PerceptualHash),
            WaveletDistance = HammingDistance(a.WaveletHash, b.WaveletHash),
        };
    }

    private static double[] ResizeToGrayscale(ImageFrame image, int size)
    {
        int srcW = (int)image.Columns;
        int srcH = (int)image.Rows;
        int channels = image.NumberOfChannels;
        double[] result = new double[size * size];

        double scaleX = (double)srcW / size;
        double scaleY = (double)srcH / size;

        for (int y = 0; y < size; y++)
        {
            int srcY = Math.Min((int)(y * scaleY), srcH - 1);
            var row = image.GetPixelRow(srcY);
            for (int x = 0; x < size; x++)
            {
                int srcX = Math.Min((int)(x * scaleX), srcW - 1);
                int off = srcX * channels;
                double lum;
                if (channels >= 3)
                {
                    lum = 0.2126 * row[off] * Quantum.Scale
                        + 0.7152 * row[off + 1] * Quantum.Scale
                        + 0.0722 * row[off + 2] * Quantum.Scale;
                }
                else
                {
                    lum = row[off] * Quantum.Scale;
                }
                result[y * size + x] = lum;
            }
        }

        return result;
    }

    /// <summary>
    /// Resize to non-square grayscale (used by dHash which needs 9×8).
    /// </summary>
    private static double[] ResizeToGrayscale(ImageFrame image, int width, int height)
    {
        int srcW = (int)image.Columns;
        int srcH = (int)image.Rows;
        int channels = image.NumberOfChannels;
        double[] result = new double[width * height];

        double scaleX = (double)srcW / width;
        double scaleY = (double)srcH / height;

        for (int y = 0; y < height; y++)
        {
            int srcY = Math.Min((int)(y * scaleY), srcH - 1);
            var row = image.GetPixelRow(srcY);
            for (int x = 0; x < width; x++)
            {
                int srcX = Math.Min((int)(x * scaleX), srcW - 1);
                int off = srcX * channels;
                double lum;
                if (channels >= 3)
                {
                    lum = 0.2126 * row[off] * Quantum.Scale
                        + 0.7152 * row[off + 1] * Quantum.Scale
                        + 0.0722 * row[off + 2] * Quantum.Scale;
                }
                else
                {
                    lum = row[off] * Quantum.Scale;
                }
                result[y * width + x] = lum;
            }
        }

        return result;
    }

    private static void ApplyDct2D(double[] input, double[] output, int n)
    {
        // Precompute cosine table
        double[] cosTable = new double[n * n];
        double factor = Math.PI / (2.0 * n);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                cosTable[i * n + j] = Math.Cos((2 * j + 1) * i * factor);
            }
        }

        double sqrt1n = 1.0 / Math.Sqrt(n);
        double sqrt2n = Math.Sqrt(2.0 / n);

        // Temp buffer for row-wise DCT
        double[] temp = new double[n * n];

        // 1D DCT on rows
        for (int row = 0; row < n; row++)
        {
            for (int u = 0; u < n; u++)
            {
                double sum = 0;
                for (int x = 0; x < n; x++)
                {
                    sum += input[row * n + x] * cosTable[u * n + x];
                }
                temp[row * n + u] = sum * (u == 0 ? sqrt1n : sqrt2n);
            }
        }

        // 1D DCT on columns
        for (int col = 0; col < n; col++)
        {
            for (int v = 0; v < n; v++)
            {
                double sum = 0;
                for (int y = 0; y < n; y++)
                {
                    sum += temp[y * n + col] * cosTable[v * n + y];
                }
                output[v * n + col] = sum * (v == 0 ? sqrt1n : sqrt2n);
            }
        }
    }

    /// <summary>
    /// In-place 2D Haar wavelet transform (one decomposition level).
    /// </summary>
    private static void HaarWavelet2D(double[] data, int size)
    {
        double[] temp = new double[size];
        double sqrt2Inv = 1.0 / Math.Sqrt(2.0);

        // Transform rows
        for (int y = 0; y < size; y++)
        {
            int half = size / 2;
            for (int i = 0; i < half; i++)
            {
                double a = data[y * size + 2 * i];
                double b = data[y * size + 2 * i + 1];
                temp[i] = (a + b) * sqrt2Inv;        // Low-pass (average)
                temp[half + i] = (a - b) * sqrt2Inv;  // High-pass (detail)
            }
            Array.Copy(temp, 0, data, y * size, size);
        }

        // Transform columns
        for (int x = 0; x < size; x++)
        {
            int half = size / 2;
            for (int i = 0; i < half; i++)
            {
                double a = data[(2 * i) * size + x];
                double b = data[(2 * i + 1) * size + x];
                temp[i] = (a + b) * sqrt2Inv;
                temp[half + i] = (a - b) * sqrt2Inv;
            }
            for (int i = 0; i < size; i++)
                data[i * size + x] = temp[i];
        }
    }
}

/// <summary>
/// Contains all four hash types for comprehensive image fingerprinting.
/// </summary>
public struct ImageHashSet
{
    public ulong AverageHash;
    public ulong DifferenceHash;
    public ulong PerceptualHash;
    public ulong WaveletHash;

    public override readonly string ToString()
    {
        return $"aHash={AverageHash:x16} dHash={DifferenceHash:x16} pHash={PerceptualHash:x16} wHash={WaveletHash:x16}";
    }
}

/// <summary>
/// Hamming distances between two ImageHashSets. 0 = identical, 64 = completely different.
/// </summary>
public struct HashComparisonResult
{
    public int AverageDistance;
    public int DifferenceDistance;
    public int PerceptualDistance;
    public int WaveletDistance;

    /// <summary>
    /// Maximum distance across all hash types. If this is low, images are very similar.
    /// </summary>
    public readonly int MaxDistance => Math.Max(Math.Max(AverageDistance, DifferenceDistance),
                                               Math.Max(PerceptualDistance, WaveletDistance));

    /// <summary>
    /// Whether images are likely similar (all distances ≤ threshold).
    /// </summary>
    public readonly bool AreSimilar(int threshold = 10) => MaxDistance <= threshold;

    public override readonly string ToString()
    {
        return $"aHash={AverageDistance} dHash={DifferenceDistance} pHash={PerceptualDistance} wHash={WaveletDistance}";
    }
}
