// SharpImage — Tensor export for ML/AI interop.
// Exports image data as CHW (Channel-Height-Width) float32 arrays
// compatible with PyTorch, TensorFlow, ONNX Runtime, etc.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Analysis;

/// <summary>
/// Exports image pixel data as tensors (multidimensional float arrays) for ML/AI pipelines.
/// Supports CHW (PyTorch), HWC (TensorFlow), and flat layouts with configurable normalization.
/// </summary>
public static class TensorExport
{
    /// <summary>Channel ordering for tensor output.</summary>
    public enum TensorLayout { CHW, HWC }

    /// <summary>Normalization mode for pixel values.</summary>
    public enum NormalizationMode
    {
        /// <summary>Values in [0, 1] range.</summary>
        ZeroToOne,
        /// <summary>Values in [-1, 1] range (common for GANs).</summary>
        NegOneToOne,
        /// <summary>ImageNet normalization: (x - mean) / std per channel.</summary>
        ImageNet,
        /// <summary>No normalization — raw 16-bit quantum values scaled to float.</summary>
        Raw
    }

    // ImageNet mean/std per channel (RGB order)
    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// Export image as a flat float32 array in the specified layout.
    /// Returns shape as (channels, height, width) or (height, width, channels).
    /// </summary>
    public static float[] ToTensor(ImageFrame source, TensorLayout layout = TensorLayout.CHW,
        NormalizationMode normalization = NormalizationMode.ZeroToOne, bool includeAlpha = false)
    {
        int height = (int)source.Rows;
        int width = (int)source.Columns;
        int srcChannels = source.HasAlpha ? 4 : 3;
        int outChannels = includeAlpha && source.HasAlpha ? 4 : 3;
        int totalElements = outChannels * height * width;

        var tensor = new float[totalElements];
        double scale = 1.0 / Quantum.MaxValue;

        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int pixelOffset = x * srcChannels;
                for (int c = 0; c < outChannels; c++)
                {
                    float value = (float)(row[pixelOffset + c] * scale);
                    value = NormalizeValue(value, c, normalization);

                    int index = layout == TensorLayout.CHW
                        ? c * height * width + y * width + x
                        : y * width * outChannels + x * outChannels + c;

                    tensor[index] = value;
                }
            }
        }

        return tensor;
    }

    /// <summary>Export image as a 3D array [channels][height][width] (CHW) or [height][width][channels] (HWC).</summary>
    public static float[][][] ToTensor3D(ImageFrame source, TensorLayout layout = TensorLayout.CHW,
        NormalizationMode normalization = NormalizationMode.ZeroToOne, bool includeAlpha = false)
    {
        int height = (int)source.Rows;
        int width = (int)source.Columns;
        int srcChannels = source.HasAlpha ? 4 : 3;
        int outChannels = includeAlpha && source.HasAlpha ? 4 : 3;
        double scale = 1.0 / Quantum.MaxValue;

        if (layout == TensorLayout.CHW)
        {
            var tensor = new float[outChannels][][];
            for (int c = 0; c < outChannels; c++)
            {
                tensor[c] = new float[height][];
                for (int y = 0; y < height; y++)
                    tensor[c][y] = new float[width];
            }

            for (int y = 0; y < height; y++)
            {
                var row = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int pixelOffset = x * srcChannels;
                    for (int c = 0; c < outChannels; c++)
                    {
                        float value = (float)(row[pixelOffset + c] * scale);
                        tensor[c][y][x] = NormalizeValue(value, c, normalization);
                    }
                }
            }

            return tensor;
        }
        else
        {
            var tensor = new float[height][][];
            for (int y = 0; y < height; y++)
            {
                tensor[y] = new float[width][];
                for (int x = 0; x < width; x++)
                    tensor[y][x] = new float[outChannels];
            }

            for (int y = 0; y < height; y++)
            {
                var row = source.GetPixelRow(y);
                for (int x = 0; x < width; x++)
                {
                    int pixelOffset = x * srcChannels;
                    for (int c = 0; c < outChannels; c++)
                    {
                        float value = (float)(row[pixelOffset + c] * scale);
                        tensor[y][x][c] = NormalizeValue(value, c, normalization);
                    }
                }
            }

            return tensor;
        }
    }

    /// <summary>Get the tensor shape for an image.</summary>
    public static (int Dim0, int Dim1, int Dim2) GetShape(ImageFrame source,
        TensorLayout layout = TensorLayout.CHW, bool includeAlpha = false)
    {
        int height = (int)source.Rows;
        int width = (int)source.Columns;
        int channels = includeAlpha && source.HasAlpha ? 4 : 3;

        return layout == TensorLayout.CHW
            ? (channels, height, width)
            : (height, width, channels);
    }

    /// <summary>
    /// Save tensor data as a raw binary file (.bin) for direct memory-mapped loading.
    /// Writes a small header followed by float32 data.
    /// Header: 4 bytes magic "SITF", 4 bytes version, 3×4 bytes shape (int32), then float32 data.
    /// </summary>
    public static void SaveBinary(ImageFrame source, string path,
        TensorLayout layout = TensorLayout.CHW,
        NormalizationMode normalization = NormalizationMode.ZeroToOne,
        bool includeAlpha = false)
    {
        var tensor = ToTensor(source, layout, normalization, includeAlpha);
        var shape = GetShape(source, layout, includeAlpha);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // Header: magic + version + shape
        writer.Write((byte)'S');
        writer.Write((byte)'I');
        writer.Write((byte)'T');
        writer.Write((byte)'F'); // SharpImage Tensor Format
        writer.Write(1); // version
        writer.Write(shape.Dim0);
        writer.Write(shape.Dim1);
        writer.Write(shape.Dim2);

        // Data
        for (int i = 0; i < tensor.Length; i++)
            writer.Write(tensor[i]);
    }

    /// <summary>
    /// Load a tensor from a SITF binary file.
    /// Returns the flat tensor data and the shape.
    /// </summary>
    public static (float[] Data, int Dim0, int Dim1, int Dim2) LoadBinary(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        // Validate magic
        byte s = reader.ReadByte(), i = reader.ReadByte(), t = reader.ReadByte(), f = reader.ReadByte();
        if (s != 'S' || i != 'I' || t != 'T' || f != 'F')
            throw new InvalidDataException("Not a valid SITF tensor file.");

        int version = reader.ReadInt32();
        if (version != 1)
            throw new InvalidDataException($"Unsupported SITF version: {version}");

        int dim0 = reader.ReadInt32();
        int dim1 = reader.ReadInt32();
        int dim2 = reader.ReadInt32();
        int total = dim0 * dim1 * dim2;

        var data = new float[total];
        for (int idx = 0; idx < total; idx++)
            data[idx] = reader.ReadSingle();

        return (data, dim0, dim1, dim2);
    }

    /// <summary>
    /// Create an ImageFrame from a CHW or HWC float32 tensor (inverse of ToTensor).
    /// Assumes ZeroToOne normalization for reconstruction.
    /// </summary>
    public static ImageFrame FromTensor(float[] tensor, int dim0, int dim1, int dim2,
        TensorLayout layout = TensorLayout.CHW)
    {
        int channels, height, width;
        if (layout == TensorLayout.CHW)
        {
            channels = dim0;
            height = dim1;
            width = dim2;
        }
        else
        {
            height = dim0;
            width = dim1;
            channels = dim2;
        }

        bool hasAlpha = channels == 4;
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int pixelOffset = x * channels;
                for (int c = 0; c < channels; c++)
                {
                    int index = layout == TensorLayout.CHW
                        ? c * height * width + y * width + x
                        : y * width * channels + x * channels + c;

                    float value = Math.Clamp(tensor[index], 0f, 1f);
                    row[pixelOffset + c] = Quantum.Clamp(value * Quantum.MaxValue);
                }
            }
        }

        return frame;
    }

    /// <summary>Export per-channel statistics (min, max, mean, std) for the tensor.</summary>
    public static ChannelStats[] GetChannelStatistics(ImageFrame source)
    {
        int height = (int)source.Rows;
        int width = (int)source.Columns;
        int srcChannels = source.HasAlpha ? 4 : 3;
        int outChannels = Math.Min(srcChannels, 3); // RGB only for stats
        double scale = 1.0 / Quantum.MaxValue;

        var stats = new ChannelStats[outChannels];
        var sums = new double[outChannels];
        var sumsSq = new double[outChannels];
        var mins = new float[outChannels];
        var maxs = new float[outChannels];
        for (int c = 0; c < outChannels; c++)
        {
            mins[c] = float.MaxValue;
            maxs[c] = float.MinValue;
        }

        long pixelCount = height * width;

        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * srcChannels;
                for (int c = 0; c < outChannels; c++)
                {
                    float v = (float)(row[offset + c] * scale);
                    sums[c] += v;
                    sumsSq[c] += v * v;
                    if (v < mins[c]) mins[c] = v;
                    if (v > maxs[c]) maxs[c] = v;
                }
            }
        }

        string[] channelNames = ["Red", "Green", "Blue", "Alpha"];
        for (int c = 0; c < outChannels; c++)
        {
            double mean = sums[c] / pixelCount;
            double variance = sumsSq[c] / pixelCount - mean * mean;
            stats[c] = new ChannelStats(
                channelNames[c],
                mins[c],
                maxs[c],
                (float)mean,
                (float)Math.Sqrt(Math.Max(0, variance))
            );
        }

        return stats;
    }

    private static float NormalizeValue(float value, int channel, NormalizationMode mode)
    {
        return mode switch
        {
            NormalizationMode.ZeroToOne => value,
            NormalizationMode.NegOneToOne => value * 2f - 1f,
            NormalizationMode.ImageNet => (value - ImageNetMean[Math.Min(channel, 2)]) / ImageNetStd[Math.Min(channel, 2)],
            NormalizationMode.Raw => value * (float)Quantum.MaxValue,
            _ => value
        };
    }
}

/// <summary>Statistics for a single tensor channel.</summary>
public readonly record struct ChannelStats(string Name, float Min, float Max, float Mean, float Std);
