// SharpImage — Channel operations: separate and combine individual color channels.

using System.Runtime.CompilerServices;
using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Channel;

/// <summary>
/// Channel separation and combination operations.
/// Separate splits an image into individual grayscale channel images.
/// Combine merges channel images back into a composite.
/// </summary>
public static class ChannelOps
{
    /// <summary>
    /// Separates an image into individual channel images.
    /// Each returned image is a single-channel grayscale representation.
    /// For RGB: returns [Red, Green, Blue]. For RGBA: returns [Red, Green, Blue, Alpha].
    /// </summary>
    public static ImageFrame[] Separate(ImageFrame source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int channels = source.NumberOfChannels;
        long cols = source.Columns;
        long rows = source.Rows;

        var results = new ImageFrame[channels];

        for (int c = 0; c < channels; c++)
        {
            var channelImage = new ImageFrame();
            channelImage.Initialize(cols, rows, ColorspaceType.SRGB, false);
            results[c] = channelImage;
        }

        for (long y = 0; y < rows; y++)
        {
            var srcRow = source.GetPixelRow(y);

            for (int c = 0; c < channels; c++)
            {
                var dstRow = results[c].GetPixelRowForWrite(y);

                for (long x = 0; x < cols; x++)
                {
                    ushort val = srcRow[(int)(x * channels + c)];
                    int dstOff = (int)(x * 3); // output is RGB grayscale (3 channels)
                    dstRow[dstOff] = val;
                    dstRow[dstOff + 1] = val;
                    dstRow[dstOff + 2] = val;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Separates a specific channel from an image, returning a grayscale representation.
    /// </summary>
    /// <param name="source">Source image</param>
    /// <param name="channelIndex">0=Red, 1=Green, 2=Blue, 3=Alpha</param>
    public static ImageFrame SeparateChannel(ImageFrame source, int channelIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (channelIndex < 0 || channelIndex >= source.NumberOfChannels)
            throw new ArgumentOutOfRangeException(nameof(channelIndex),
                $"Channel index {channelIndex} is out of range for image with {source.NumberOfChannels} channels");

        long cols = source.Columns;
        long rows = source.Rows;
        int srcChannels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(cols, rows, ColorspaceType.SRGB, false);

        for (long y = 0; y < rows; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);

            for (long x = 0; x < cols; x++)
            {
                ushort val = srcRow[(int)(x * srcChannels + channelIndex)];
                int dstOff = (int)(x * 3);
                dstRow[dstOff] = val;
                dstRow[dstOff + 1] = val;
                dstRow[dstOff + 2] = val;
            }
        }

        return result;
    }

    /// <summary>
    /// Combines separate channel images into a single composite image.
    /// Accepts 3 images (RGB) or 4 images (RGBA).
    /// Each input should be a grayscale image where the red channel value is used.
    /// </summary>
    public static ImageFrame Combine(ImageFrame[] channelImages)
    {
        ArgumentNullException.ThrowIfNull(channelImages);
        if (channelImages.Length < 3 || channelImages.Length > 4)
            throw new ArgumentException("Combine requires 3 (RGB) or 4 (RGBA) channel images", nameof(channelImages));

        long cols = channelImages[0].Columns;
        long rows = channelImages[0].Rows;
        bool hasAlpha = channelImages.Length == 4;

        // Validate all images are the same size
        for (int i = 1; i < channelImages.Length; i++)
        {
            if (channelImages[i].Columns != cols || channelImages[i].Rows != rows)
                throw new ArgumentException(
                    $"Channel image {i} dimensions ({channelImages[i].Columns}×{channelImages[i].Rows}) " +
                    $"do not match first image ({cols}×{rows})");
        }

        var result = new ImageFrame();
        result.Initialize(cols, rows, ColorspaceType.SRGB, hasAlpha);
        int dstChannels = result.NumberOfChannels;

        for (long y = 0; y < rows; y++)
        {
            var dstRow = result.GetPixelRowForWrite(y);

            // Read the red channel (index 0) from each grayscale channel image
            var rRow = channelImages[0].GetPixelRow(y);
            var gRow = channelImages[1].GetPixelRow(y);
            var bRow = channelImages[2].GetPixelRow(y);
            ReadOnlySpan<ushort> aRow = hasAlpha ? channelImages[3].GetPixelRow(y) : default;

            int rChannels = channelImages[0].NumberOfChannels;
            int gChannels = channelImages[1].NumberOfChannels;
            int bChannels = channelImages[2].NumberOfChannels;
            int aChannels = hasAlpha ? channelImages[3].NumberOfChannels : 0;

            for (long x = 0; x < cols; x++)
            {
                int dstOff = (int)(x * dstChannels);
                // Take the first channel (red) from each grayscale image
                dstRow[dstOff] = rRow[(int)(x * rChannels)];
                dstRow[dstOff + 1] = gRow[(int)(x * gChannels)];
                dstRow[dstOff + 2] = bRow[(int)(x * bChannels)];
                if (hasAlpha)
                {
                    dstRow[dstOff + 3] = aRow[(int)(x * aChannels)];
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Swaps two channels in an image (in-place).
    /// </summary>
    /// <param name="image">The image to modify</param>
    /// <param name="channel1">First channel index (0=R, 1=G, 2=B, 3=A)</param>
    /// <param name="channel2">Second channel index</param>
    public static void SwapChannels(ImageFrame image, int channel1, int channel2)
    {
        ArgumentNullException.ThrowIfNull(image);
        int channels = image.NumberOfChannels;
        if (channel1 < 0 || channel1 >= channels)
            throw new ArgumentOutOfRangeException(nameof(channel1));
        if (channel2 < 0 || channel2 >= channels)
            throw new ArgumentOutOfRangeException(nameof(channel2));
        if (channel1 == channel2) return;

        for (long y = 0; y < image.Rows; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (long x = 0; x < image.Columns; x++)
            {
                int off = (int)(x * channels);
                (row[off + channel1], row[off + channel2]) = (row[off + channel2], row[off + channel1]);
            }
        }
    }

    /// <summary>
    /// Gets the name of a channel by its index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetChannelName(int index, bool hasAlpha) => index switch
    {
        0 => "Red",
        1 => "Green",
        2 => "Blue",
        3 when hasAlpha => "Alpha",
        _ => $"Channel{index}"
    };
}
