using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// Shared processing pipeline for camera raw images. Applies black level subtraction,
/// white balance, camera-to-sRGB color matrix transformation, and gamma correction.
/// Used by all camera raw format decoders after demosaicing.
/// </summary>
public static class CameraRawProcessor
{
    /// <summary>
    /// XYZ D50 to linear sRGB matrix (Bradford-adapted from D50 to D65).
    /// Used for DNG-embedded ColorMatrix1 values which are calibrated for D50 illuminant
    /// per the DNG specification.
    /// </summary>
    private static readonly double[] XyzD50ToSrgbMatrix =
    [
         3.1338561, -1.6168667, -0.4906146,
        -0.9787684,  1.9161415,  0.0334540,
         0.0719453, -0.2289914,  1.4052427
    ];

    /// <summary>
    /// XYZ D65 to linear sRGB matrix (standard IEC 61966-2-1).
    /// Used for DNG-embedded ColorMatrix1 values when combined with D65 illuminant.
    /// </summary>
    private static readonly double[] XyzD65ToSrgbMatrix =
    [
         3.2404542, -1.5371385, -0.4985314,
        -0.9692660,  1.8760108,  0.0415560,
         0.0556434, -0.2040259,  1.0572252
    ];

    /// <summary>
    /// Linear sRGB to XYZ D65 matrix (sRGB primaries under D65 illuminant).
    /// Used in the dcraw pipeline: cam_rgb = cam_xyz × srgb_to_xyz, then
    /// row-normalize cam_rgb, then invert to get Camera→sRGB.
    /// These are dcraw's exact "xyz_rgb" coefficients (named confusingly in dcraw source;
    /// they actually transform FROM sRGB TO XYZ).
    /// </summary>
    private static readonly double[] SrgbToXyzD65Matrix =
    [
        0.412453, 0.357580, 0.180423,
        0.212671, 0.715160, 0.072169,
        0.019334, 0.119193, 0.950227
    ];

    /// <summary>
    /// Applies the full camera raw processing pipeline to a demosaiced RGB image.
    /// Pipeline: white balance → color matrix → highlight recovery → auto-brightness → gamma.
    /// When a color matrix is available, WB multipliers are absorbed into the matrix
    /// (matching dcraw/libraw behavior) to avoid double-applying WB correction.
    /// Modifies the ImageFrame in-place for zero-copy performance.
    /// </summary>
    public static void Process(ImageFrame demosaicedImage, in CameraRawMetadata metadata, in CameraRawDecodeOptions options)
    {
        bool hasColorMatrix = options.ApplyColorMatrix &&
            (metadata.ColorMatrix1 is { Length: 9 } || metadata.ForwardMatrix1 is { Length: 9 });

        if (hasColorMatrix)
        {
            if (metadata.WbAppliedAtBayerLevel)
            {
                // WB already applied to raw Bayer data (dcraw pipeline match).
                // Apply color matrix WITHOUT absorbing WB — it's already in the pixel data.
                ApplyColorMatrix(demosaicedImage, in metadata);
            }
            else
            {
                // No pre-demosaic WB (e.g., Auto WB mode). Absorb WB into matrix.
                ApplyColorMatrixWithWb(demosaicedImage, in metadata, in options);
            }
        }
        else if (!metadata.WbAppliedAtBayerLevel)
        {
            // No color matrix and no pre-demosaic WB — apply WB separately
            ApplyWhiteBalance(demosaicedImage, in metadata, in options);
        }

        if (options.HighlightRecovery > 0)
            RecoverHighlights(demosaicedImage, options.HighlightRecovery);

        // Auto-brightness: scale histogram so the 99th percentile reaches near-white.
        // Matches libraw's default behavior (no_auto_bright=0, auto_bright_thr=0.01).
        // Without this step, images appear too dark because ScaleToFullRange intentionally
        // leaves headroom for WB to boost channels without clipping.
        if (options.ApplyGamma)
            ApplyAutoBrightness(demosaicedImage);

        if (options.ApplyGamma)
            ApplyGamma(demosaicedImage);
    }

    /// <summary>
    /// Subtracts black level from raw Bayer sensor data and scales to full 16-bit range.
    /// Applies linearization table if present. Call BEFORE demosaicing.
    /// Modifies RawSensorData.RawPixels in-place.
    /// </summary>
    public static void ScaleToFullRange(ref RawSensorData raw)
    {
        ushort[] pixels = raw.RawPixels;
        int length = pixels.Length;
        CameraRawMetadata metadata = raw.Metadata;

        // Apply linearization table if present (maps non-linear sensor response to linear)
        if (metadata.LinearizationTable is { Length: > 0 } lut)
        {
            int lutMax = lut.Length - 1;
            for (int i = 0; i < length; i++)
            {
                int value = pixels[i];
                pixels[i] = value <= lutMax ? lut[value] : lut[lutMax];
            }
        }

        int blackLevel = metadata.BlackLevel;
        int maxRawValue = metadata.WhiteLevel > 0
            ? metadata.WhiteLevel
            : (1 << metadata.BitsPerSample) - 1;

        if (maxRawValue <= 0 || (blackLevel == 0 && maxRawValue >= 65535))
            return;

        // Subtract black level and scale using whiteLevel as divisor (matching libraw's scale_colors).
        // libraw uses: output = (raw - black) * pre_mul * 65535 / maximum
        // This intentionally leaves headroom: a pixel at whiteLevel maps to
        // (whiteLevel - black) / whiteLevel * 65535, not 65535. The headroom allows
        // WB multipliers > 1.0 to boost channels without immediately clipping.
        // Auto-brightness then scales the histogram to fill the output range.
        for (int i = 0; i < length; i++)
        {
            long value = pixels[i] - blackLevel;
            if (value <= 0)
                pixels[i] = 0;
            else
                pixels[i] = (ushort)Math.Min(value * 65535L / maxRawValue, 65535L);
        }
    }

    /// <summary>
    /// Resolves white balance multipliers from metadata without requiring a demosaiced image.
    /// Returns true if WB was resolved, false if Auto WB was requested (needs demosaiced image).
    /// Normalizes so the minimum multiplier is 1.0 (dcraw's pre_mul normalization).
    /// </summary>
    public static bool TryResolveWhiteBalanceFromMetadata(in CameraRawMetadata metadata,
        in CameraRawDecodeOptions options, out double wbR, out double wbG, out double wbB)
    {
        wbR = 1.0; wbG = 1.0; wbB = 1.0;

        switch (options.WhiteBalance)
        {
            case WhiteBalanceMode.AsShot when metadata.AsShotWhiteBalance is { Length: >= 3 }:
                wbR = metadata.AsShotWhiteBalance[0];
                wbG = metadata.AsShotWhiteBalance[1];
                wbB = metadata.AsShotWhiteBalance[2];
                break;
            case WhiteBalanceMode.Daylight when metadata.DaylightWhiteBalance is { Length: >= 3 }:
                wbR = metadata.DaylightWhiteBalance[0];
                wbG = metadata.DaylightWhiteBalance[1];
                wbB = metadata.DaylightWhiteBalance[2];
                break;
            case WhiteBalanceMode.Custom:
                wbR = options.CustomWbRed;
                wbG = options.CustomWbGreen;
                wbB = options.CustomWbBlue;
                break;
            case WhiteBalanceMode.Auto:
                return false; // Auto WB needs a demosaiced image for statistics
            default:
                // AsShot requested but no data — fallback chain matching dcraw:
                // 1. Daylight WB from metadata (embedded by camera/format)
                // 2. Matrix-based daylight WB (pre_mul from color matrix column sums)
                // 3. Return false (Auto WB computed post-demosaic as last resort)
                if (metadata.DaylightWhiteBalance is { Length: >= 3 })
                {
                    wbR = metadata.DaylightWhiteBalance[0];
                    wbG = metadata.DaylightWhiteBalance[1];
                    wbB = metadata.DaylightWhiteBalance[2];
                }
                else if (metadata.ColorMatrix1 is { Length: 9 })
                {
                    (wbR, wbG, wbB) = ComputeDaylightWbFromColorMatrix(metadata.ColorMatrix1);
                }
                else
                {
                    return false;
                }
                break;
        }

        // Normalize so minimum = 1.0 (dcraw's pre_mul normalization).
        // Channels with multiplier > 1.0 may clip, handled by HighlightRecovery later.
        double wbMin = Math.Min(wbR, Math.Min(wbG, wbB));
        if (wbMin > 0)
        {
            wbR /= wbMin;
            wbG /= wbMin;
            wbB /= wbMin;
        }

        return true;
    }

    /// <summary>
    /// Applies white balance multipliers to raw Bayer sensor data BEFORE demosaicing.
    /// This matches dcraw's scale_colors() approach: WB is applied to the raw CFA data
    /// so the demosaic algorithm interpolates correctly balanced channel values.
    /// Without pre-demosaic WB, bilinear interpolation mixes unbalanced R/G/B values,
    /// producing poor color separation (e.g., purple cast on Canon CR2 images).
    /// </summary>
    public static void ApplyWhiteBalanceToBayer(ref RawSensorData raw, double wbR, double wbG, double wbB)
    {
        if (raw.Metadata.CfaType != CfaType.Bayer)
            return;

        // Skip if WB is effectively identity
        if (Math.Abs(wbR - 1.0) < 1e-6 && Math.Abs(wbG - 1.0) < 1e-6 && Math.Abs(wbB - 1.0) < 1e-6)
            return;

        ushort[] pixels = raw.RawPixels;
        int width = raw.Width;
        int height = raw.Height;
        BayerPattern pattern = raw.Metadata.BayerPattern;

        // Map each 2×2 position to its WB multiplier based on the CFA pattern.
        // (y&1, x&1) gives: (0,0)=topLeft, (0,1)=topRight, (1,0)=bottomLeft, (1,1)=bottomRight
        double m00, m01, m10, m11;
        switch (pattern)
        {
            case BayerPattern.RGGB:
                m00 = wbR; m01 = wbG; m10 = wbG; m11 = wbB; break;
            case BayerPattern.BGGR:
                m00 = wbB; m01 = wbG; m10 = wbG; m11 = wbR; break;
            case BayerPattern.GRBG:
                m00 = wbG; m01 = wbR; m10 = wbB; m11 = wbG; break;
            case BayerPattern.GBRG:
                m00 = wbG; m01 = wbB; m10 = wbR; m11 = wbG; break;
            default:
                return;
        }

        for (int y = 0; y < height; y++)
        {
            double mEven = (y & 1) == 0 ? m00 : m10;
            double mOdd = (y & 1) == 0 ? m01 : m11;
            int rowStart = y * width;

            for (int x = 0; x < width; x++)
            {
                double multiplier = (x & 1) == 0 ? mEven : mOdd;
                long value = (long)(pixels[rowStart + x] * multiplier);
                pixels[rowStart + x] = (ushort)Math.Min(value, 65535L);
            }
        }
    }

    /// <summary>
    /// Applies white balance multipliers to the RGB image.
    /// </summary>
    private static void ApplyWhiteBalance(ImageFrame image, in CameraRawMetadata metadata, in CameraRawDecodeOptions options)
    {
        double wbR, wbG, wbB;

        switch (options.WhiteBalance)
        {
            case WhiteBalanceMode.AsShot:
                if (metadata.AsShotWhiteBalance is { Length: >= 3 })
                {
                    wbR = metadata.AsShotWhiteBalance[0];
                    wbG = metadata.AsShotWhiteBalance[1];
                    wbB = metadata.AsShotWhiteBalance[2];
                }
                else
                {
                    // No as-shot WB available — fall back to auto WB
                    (wbR, wbG, wbB) = ComputeAutoWhiteBalance(image);
                }
                break;

            case WhiteBalanceMode.Daylight:
                if (metadata.DaylightWhiteBalance is { Length: >= 3 })
                {
                    wbR = metadata.DaylightWhiteBalance[0];
                    wbG = metadata.DaylightWhiteBalance[1];
                    wbB = metadata.DaylightWhiteBalance[2];
                }
                else
                {
                    return; // D65 neutral — no adjustment needed
                }
                break;

            case WhiteBalanceMode.Custom:
                wbR = options.CustomWbRed;
                wbG = options.CustomWbGreen;
                wbB = options.CustomWbBlue;
                break;

            case WhiteBalanceMode.Auto:
                (wbR, wbG, wbB) = ComputeAutoWhiteBalance(image);
                break;

            default:
                return;
        }

        // Normalize so green channel multiplier = 1.0
        if (wbG > 0.0)
        {
            wbR /= wbG;
            wbB /= wbG;
            wbG = 1.0;
        }

        if (Math.Abs(wbR - 1.0) < 1e-6 && Math.Abs(wbB - 1.0) < 1e-6)
            return;

        int channels = image.NumberOfChannels;
        long rows = image.Rows;
        long columns = image.Columns;

        for (long y = 0; y < rows; y++)
        {
            Span<ushort> row = image.GetPixelRowForWrite(y);

            for (long x = 0; x < columns; x++)
            {
                int offset = (int)(x * channels);
                row[offset]     = ClampToUshort((long)(row[offset] * wbR));
                row[offset + 1] = ClampToUshort((long)(row[offset + 1] * wbG));
                row[offset + 2] = ClampToUshort((long)(row[offset + 2] * wbB));
            }
        }
    }

    /// <summary>
    /// Computes daylight white balance multipliers from a camera color matrix (XYZ→Camera)
    /// using dcraw's exact approach: cam_rgb = cam_xyz × srgb_to_xyz, then pre_mul[i] = 1/row_sum(cam_rgb[i]).
    /// This gives physics-based daylight WB multipliers that correct for the sensor's
    /// spectral response under D65 illumination.
    /// </summary>
    private static (double r, double g, double b) ComputeDaylightWbFromColorMatrix(ReadOnlySpan<double> colorMatrix)
    {
        // dcraw's cam_xyz_coeff(): cam_rgb = cam_xyz × xyz_rgb (srgb_to_xyz)
        ReadOnlySpan<double> srgbToXyz = SrgbToXyzD65Matrix;
        Span<double> camRgb = stackalloc double[9];

        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                camRgb[i * 3 + j] =
                    colorMatrix[i * 3 + 0] * srgbToXyz[0 * 3 + j] +
                    colorMatrix[i * 3 + 1] * srgbToXyz[1 * 3 + j] +
                    colorMatrix[i * 3 + 2] * srgbToXyz[2 * 3 + j];
            }
        }

        // dcraw: pre_mul[i] = 1 / row_sum(cam_rgb[i])
        double sumR = camRgb[0] + camRgb[1] + camRgb[2];
        double sumG = camRgb[3] + camRgb[4] + camRgb[5];
        double sumB = camRgb[6] + camRgb[7] + camRgb[8];

        if (Math.Abs(sumR) < 1e-10 || Math.Abs(sumG) < 1e-10 || Math.Abs(sumB) < 1e-10)
            return (1.0, 1.0, 1.0);

        double preMulR = 1.0 / sumR;
        double preMulG = 1.0 / sumG;
        double preMulB = 1.0 / sumB;

        // Normalize to Green = 1.0
        return (preMulR / preMulG, 1.0, preMulB / preMulG);
    }

    /// <summary>
    /// Computes auto white balance multipliers using the grey world assumption.
    /// Averages all channel values and derives multipliers that equalize them.
    /// </summary>
    private static (double r, double g, double b) ComputeAutoWhiteBalance(ImageFrame image)
    {
        long sumR = 0, sumG = 0, sumB = 0;
        int channels = image.NumberOfChannels;
        long rows = image.Rows;
        long columns = image.Columns;

        for (long y = 0; y < rows; y++)
        {
            ReadOnlySpan<ushort> row = image.GetPixelRow(y);

            for (long x = 0; x < columns; x++)
            {
                int offset = (int)(x * channels);
                sumR += row[offset];
                sumG += row[offset + 1];
                sumB += row[offset + 2];
            }
        }

        long pixelCount = columns * rows;
        if (pixelCount == 0)
            return (1.0, 1.0, 1.0);

        double avgR = (double)sumR / pixelCount;
        double avgG = (double)sumG / pixelCount;
        double avgB = (double)sumB / pixelCount;

        double r = avgR > 0.0 ? avgG / avgR : 1.0;
        double b = avgB > 0.0 ? avgG / avgB : 1.0;

        return (r, 1.0, b);
    }

    /// <summary>
    /// Builds the Camera→sRGB color conversion matrix from metadata.
    /// Handles three matrix sources: ForwardMatrix (dcraw adobe_coeff), ColorMatrixIsD65
    /// (dcraw pipeline), and DNG-embedded ColorMatrix1 (D50-calibrated).
    /// </summary>
    private static bool BuildColorMatrix(in CameraRawMetadata metadata, Span<double> combined)
    {
        bool hasForwardMatrix = metadata.ForwardMatrix1 is { Length: 9 };

        if (hasForwardMatrix)
        {
            // dcraw adobe_coeff matrices: use DIRECTLY as camera→output with row normalization.
            metadata.ForwardMatrix1!.AsSpan().CopyTo(combined);
        }
        else if (metadata.ColorMatrixIsD65)
        {
            // dcraw adobe_coeff pipeline (D65-calibrated matrices):
            //   1. cam_rgb = cam_xyz × srgb_to_xyz  (sRGB→Camera direction)
            //   2. Row-normalize cam_rgb (each row sums to 1.0)
            //   3. rgb_cam = inverse(cam_rgb)  (Camera→sRGB)
            ReadOnlySpan<double> camXyz = metadata.ColorMatrix1!;
            ReadOnlySpan<double> srgbToXyz = SrgbToXyzD65Matrix;
            Span<double> camRgb = stackalloc double[9];

            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    camRgb[i * 3 + j] =
                        camXyz[i * 3 + 0] * srgbToXyz[0 * 3 + j] +
                        camXyz[i * 3 + 1] * srgbToXyz[1 * 3 + j] +
                        camXyz[i * 3 + 2] * srgbToXyz[2 * 3 + j];
                }
            }

            // Row-normalize cam_rgb BEFORE inversion (dcraw's key step)
            for (int i = 0; i < 3; i++)
            {
                double rowSum = camRgb[i * 3 + 0] + camRgb[i * 3 + 1] + camRgb[i * 3 + 2];
                if (Math.Abs(rowSum) > 1e-10)
                {
                    camRgb[i * 3 + 0] /= rowSum;
                    camRgb[i * 3 + 1] /= rowSum;
                    camRgb[i * 3 + 2] /= rowSum;
                }
            }

            if (!Invert3x3(camRgb, combined))
                return false;
        }
        else
        {
            // DNG-embedded ColorMatrix1 (D50-calibrated): standard path.
            Span<double> cameraToXyz = stackalloc double[9];
            if (!Invert3x3(metadata.ColorMatrix1!, cameraToXyz))
                return false;

            ReadOnlySpan<double> xyzToSrgb = XyzD50ToSrgbMatrix;

            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    combined[i * 3 + j] =
                        xyzToSrgb[i * 3 + 0] * cameraToXyz[0 * 3 + j] +
                        xyzToSrgb[i * 3 + 1] * cameraToXyz[1 * 3 + j] +
                        xyzToSrgb[i * 3 + 2] * cameraToXyz[2 * 3 + j];
                }
            }
        }

        // Normalize each row so its elements sum to 1.0 (dcraw's rgb_cam normalization).
        for (int i = 0; i < 3; i++)
        {
            double rowSum = combined[i * 3 + 0] + combined[i * 3 + 1] + combined[i * 3 + 2];
            if (Math.Abs(rowSum) > 1e-10)
            {
                combined[i * 3 + 0] /= rowSum;
                combined[i * 3 + 1] /= rowSum;
                combined[i * 3 + 2] /= rowSum;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies the 3×3 color matrix to every pixel in the image.
    /// </summary>
    private static void ApplyMatrixToPixels(ImageFrame image, ReadOnlySpan<double> combined)
    {
        double m00 = combined[0], m01 = combined[1], m02 = combined[2];
        double m10 = combined[3], m11 = combined[4], m12 = combined[5];
        double m20 = combined[6], m21 = combined[7], m22 = combined[8];

        int channels = image.NumberOfChannels;
        long rows = image.Rows;
        long columns = image.Columns;

        for (long y = 0; y < rows; y++)
        {
            Span<ushort> row = image.GetPixelRowForWrite(y);

            for (long x = 0; x < columns; x++)
            {
                int offset = (int)(x * channels);
                double r = row[offset];
                double g = row[offset + 1];
                double b = row[offset + 2];

                double newR = m00 * r + m01 * g + m02 * b;
                double newG = m10 * r + m11 * g + m12 * b;
                double newB = m20 * r + m21 * g + m22 * b;

                row[offset]     = ClampToUshort((long)newR);
                row[offset + 1] = ClampToUshort((long)newG);
                row[offset + 2] = ClampToUshort((long)newB);
            }
        }
    }

    /// <summary>
    /// Applies camera-to-sRGB color matrix WITHOUT white balance.
    /// Used when WB was already applied at the Bayer level (dcraw pipeline match).
    /// </summary>
    private static void ApplyColorMatrix(ImageFrame image, in CameraRawMetadata metadata)
    {
        Span<double> combined = stackalloc double[9];
        if (!BuildColorMatrix(in metadata, combined))
            return;

        ApplyMatrixToPixels(image, combined);
    }

    /// <summary>
    /// Applies camera-to-sRGB color matrix with white balance absorbed into the matrix.
    /// Used when WB was NOT applied at the Bayer level (Auto WB or fallback path).
    /// </summary>
    private static void ApplyColorMatrixWithWb(ImageFrame image, in CameraRawMetadata metadata,
        in CameraRawDecodeOptions options)
    {
        Span<double> combined = stackalloc double[9];
        if (!BuildColorMatrix(in metadata, combined))
            return;

        // Absorb WB into the combined matrix columns.
        ResolveWhiteBalance(image, in metadata, in options,
            out double wbR, out double wbG, out double wbB);

        for (int i = 0; i < 3; i++)
        {
            combined[i * 3 + 0] *= wbR;
            combined[i * 3 + 1] *= wbG;
            combined[i * 3 + 2] *= wbB;
        }

        ApplyMatrixToPixels(image, combined);
    }

    /// <summary>
    /// Resolves white balance multipliers from metadata and options.
    /// Normalizes so the minimum multiplier is 1.0 (avoids darkening any channel).
    /// </summary>
    private static void ResolveWhiteBalance(ImageFrame image, in CameraRawMetadata metadata,
        in CameraRawDecodeOptions options, out double wbR, out double wbG, out double wbB)
    {
        wbR = 1.0; wbG = 1.0; wbB = 1.0;

        switch (options.WhiteBalance)
        {
            case WhiteBalanceMode.AsShot when metadata.AsShotWhiteBalance is { Length: >= 3 }:
                wbR = metadata.AsShotWhiteBalance[0];
                wbG = metadata.AsShotWhiteBalance[1];
                wbB = metadata.AsShotWhiteBalance[2];
                break;
            case WhiteBalanceMode.Daylight when metadata.DaylightWhiteBalance is { Length: >= 3 }:
                wbR = metadata.DaylightWhiteBalance[0];
                wbG = metadata.DaylightWhiteBalance[1];
                wbB = metadata.DaylightWhiteBalance[2];
                break;
            case WhiteBalanceMode.Custom:
                wbR = options.CustomWbRed;
                wbG = options.CustomWbGreen;
                wbB = options.CustomWbBlue;
                break;
            case WhiteBalanceMode.Auto:
                (wbR, wbG, wbB) = ComputeAutoWhiteBalance(image);
                break;
            default:
                // AsShot requested but no data available — fallback chain matching dcraw:
                // 1. Daylight WB from metadata (embedded by camera/format)
                // 2. Matrix-based daylight WB (pre_mul from color matrix column sums)
                // 3. Auto grey-world (last resort — unreliable for many subjects)
                if (metadata.DaylightWhiteBalance is { Length: >= 3 })
                {
                    wbR = metadata.DaylightWhiteBalance[0];
                    wbG = metadata.DaylightWhiteBalance[1];
                    wbB = metadata.DaylightWhiteBalance[2];
                }
                else if (metadata.ColorMatrix1 is { Length: 9 })
                {
                    // dcraw uses pre_mul (column sums of cam_xyz inverse) as default WB
                    // when no camera WB is available. This is physics-based and much more
                    // reliable than grey-world auto WB which depends on scene content.
                    (wbR, wbG, wbB) = ComputeDaylightWbFromColorMatrix(metadata.ColorMatrix1);
                }
                else
                {
                    (wbR, wbG, wbB) = ComputeAutoWhiteBalance(image);
                }
                break;
        }

        // Normalize so minimum = 1.0 (no channel is darkened, matching dcraw's pre_mul behavior).
        // This may cause highlights to clip, which HighlightRecovery handles downstream.
        double wbMin = Math.Min(wbR, Math.Min(wbG, wbB));
        if (wbMin > 0)
        {
            wbR /= wbMin;
            wbG /= wbMin;
            wbB /= wbMin;
        }
    }

    /// <summary>
    /// Recovers clipped highlights by blending information from non-clipped channels.
    /// Mode 0: clip (do nothing), Mode 1: unclip, Mode 2: blend.
    /// </summary>
    private static void RecoverHighlights(ImageFrame image, int mode)
    {
        if (mode <= 0)
            return;

        const ushort clipThreshold = 65000;
        const ushort blendZoneStart = 60000;
        int channels = image.NumberOfChannels;
        long rows = image.Rows;
        long columns = image.Columns;

        for (long y = 0; y < rows; y++)
        {
            Span<ushort> row = image.GetPixelRowForWrite(y);

            for (long x = 0; x < columns; x++)
            {
                int offset = (int)(x * channels);
                ushort r = row[offset];
                ushort g = row[offset + 1];
                ushort b = row[offset + 2];

                bool rClipped = r > clipThreshold;
                bool gClipped = g > clipThreshold;
                bool bClipped = b > clipThreshold;

                int clippedCount = (rClipped ? 1 : 0) + (gClipped ? 1 : 0) + (bClipped ? 1 : 0);

                if (clippedCount == 0)
                    continue;

                if (clippedCount == 3)
                {
                    row[offset]     = Quantum.MaxValue;
                    row[offset + 1] = Quantum.MaxValue;
                    row[offset + 2] = Quantum.MaxValue;
                    continue;
                }

                // Compute average of non-clipped channels for reconstruction
                long nonClippedSum = 0;
                int nonClippedChannels = 0;
                if (!rClipped) { nonClippedSum += r; nonClippedChannels++; }
                if (!gClipped) { nonClippedSum += g; nonClippedChannels++; }
                if (!bClipped) { nonClippedSum += b; nonClippedChannels++; }

                ushort reconstructed = (ushort)(nonClippedSum / nonClippedChannels);

                if (mode == 1)
                {
                    // Unclip: replace clipped channels with reconstructed value
                    if (rClipped) row[offset]     = reconstructed;
                    if (gClipped) row[offset + 1] = reconstructed;
                    if (bClipped) row[offset + 2] = reconstructed;
                }
                else
                {
                    // Blend: smooth transition in the near-clipped zone
                    row[offset]     = BlendHighlight(r, reconstructed, blendZoneStart, rClipped);
                    row[offset + 1] = BlendHighlight(g, reconstructed, blendZoneStart, gClipped);
                    row[offset + 2] = BlendHighlight(b, reconstructed, blendZoneStart, bClipped);
                }
            }
        }
    }

    /// <summary>
    /// Blends an original channel value toward a reconstructed value in the highlight zone.
    /// Clipped channels are replaced; near-clipped channels are smoothly transitioned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort BlendHighlight(ushort original, ushort reconstructed, ushort blendZoneStart, bool isClipped)
    {
        if (isClipped)
            return reconstructed;

        if (original <= blendZoneStart)
            return original;

        // Linear blend from blendZoneStart (100% original) to clipThreshold (100% reconstructed)
        double t = (double)(original - blendZoneStart) / (65000 - blendZoneStart);
        return (ushort)(original * (1.0 - t) + reconstructed * t);
    }

    /// <summary>
    /// Scales the image so the 99th percentile of pixel brightness reaches near-white.
    /// Matches libraw's get_auto_bright_multiplier() exactly:
    ///   1. Build per-channel histograms (8192 bins each for R, G, B)
    ///   2. Per channel: walk top-down until cumulative count exceeds (pixelCount × 0.01)
    ///   3. white = max(percentileBin/8192) across all channels
    ///   4. multiplier = 1.0 / white
    /// </summary>
    private static void ApplyAutoBrightness(ImageFrame image)
    {
        const int histSize = 0x2000; // 8192 bins
        int[] histR = new int[histSize];
        int[] histG = new int[histSize];
        int[] histB = new int[histSize];
        int channels = image.NumberOfChannels;
        long rows = image.Rows;
        long columns = image.Columns;
        long totalPixels = rows * columns;

        // Build per-channel histograms matching libraw: FORCC histogram[c][img[c] >> 3]++
        for (long y = 0; y < rows; y++)
        {
            ReadOnlySpan<ushort> row = image.GetPixelRow(y);
            for (long x = 0; x < columns; x++)
            {
                int offset = (int)(x * channels);
                histR[row[offset] >> 3]++;
                histG[row[offset + 1] >> 3]++;
                histB[row[offset + 2] >> 3]++;
            }
        }

        // Per channel: find percentile bin where top-down cumulative exceeds threshold.
        // threshold = pixelCount × 0.01 (libraw: iheight * iwidth * auto_bright_thr)
        int threshold = (int)(totalPixels * 0.01);
        double white = 0;

        int[][] hists = [histR, histG, histB];
        for (int c = 0; c < 3; c++)
        {
            int[] hist = hists[c];
            int cumulative = 0;
            for (int val = histSize - 1; val > 32; val--)
            {
                cumulative += hist[val];
                if (cumulative > threshold)
                {
                    double r = (double)val / histSize;
                    if (r > white)
                        white = r;
                    break;
                }
            }
        }

        if (white <= 0)
            return;

        double bright = 1.0 / white;

        // Don't apply if already near-optimal or would barely change
        if (bright <= 1.02)
            return;

        // Cap extreme scaling to avoid noise amplification
        if (bright > 8.0)
            bright = 8.0;

        // Apply brightness multiplier to all pixels
        for (long y = 0; y < rows; y++)
        {
            Span<ushort> row = image.GetPixelRowForWrite(y);
            for (long x = 0; x < columns; x++)
            {
                int offset = (int)(x * channels);
                row[offset]     = ClampToUshort((long)(row[offset] * bright));
                row[offset + 1] = ClampToUshort((long)(row[offset + 1] * bright));
                row[offset + 2] = ClampToUshort((long)(row[offset + 2] * bright));
            }
        }
    }

    /// <summary>
    /// Applies BT.709 gamma curve to convert from linear light to display-referred output.
    /// Uses a pre-computed 65536-entry lookup table for speed.
    /// BT.709 is the default gamma curve in dcraw/libraw, matching ImageMagick's raw output.
    /// </summary>
    private static void ApplyGamma(ImageFrame image)
    {
        ushort[] gammaLut = BuildGammaLut();

        int channels = image.NumberOfChannels;
        long rows = image.Rows;
        long columns = image.Columns;

        for (long y = 0; y < rows; y++)
        {
            Span<ushort> row = image.GetPixelRowForWrite(y);

            for (long x = 0; x < columns; x++)
            {
                int offset = (int)(x * channels);
                row[offset]     = gammaLut[row[offset]];
                row[offset + 1] = gammaLut[row[offset + 1]];
                row[offset + 2] = gammaLut[row[offset + 2]];
            }
        }
    }

    /// <summary>
    /// Builds a 65536-entry lookup table for BT.709 gamma (ITU-R BT.709).
    /// This is dcraw/libraw's default gamma: power=1/2.222 (0.45), toe_slope=4.5.
    /// The linear toe region extends to L &lt; 0.018, above which the power curve applies:
    ///   V = 4.500 × L               (L &lt; 0.018)
    ///   V = 1.099 × L^0.45 - 0.099  (L ≥ 0.018)
    /// </summary>
    private static ushort[] BuildGammaLut()
    {
        var lut = new ushort[65536];

        for (int i = 0; i < 65536; i++)
        {
            double linear = i / 65535.0;
            double encoded;

            if (linear < 0.018)
                encoded = 4.5 * linear;
            else
                encoded = 1.099 * Math.Pow(linear, 0.45) - 0.099;

            lut[i] = (ushort)(Math.Clamp(encoded, 0.0, 1.0) * 65535.0 + 0.5);
        }

        return lut;
    }

    /// <summary>
    /// Inverts a 3×3 matrix using Cramer's rule (cofactor/determinant method).
    /// Returns false if the matrix is singular (determinant ≈ 0).
    /// </summary>
    private static bool Invert3x3(ReadOnlySpan<double> m, Span<double> inv)
    {
        double a = m[0], b = m[1], c = m[2];
        double d = m[3], e = m[4], f = m[5];
        double g = m[6], h = m[7], i = m[8];

        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-12)
            return false;

        double invDet = 1.0 / det;
        inv[0] = (e * i - f * h) * invDet;
        inv[1] = (c * h - b * i) * invDet;
        inv[2] = (b * f - c * e) * invDet;
        inv[3] = (f * g - d * i) * invDet;
        inv[4] = (a * i - c * g) * invDet;
        inv[5] = (c * d - a * f) * invDet;
        inv[6] = (d * h - e * g) * invDet;
        inv[7] = (b * g - a * h) * invDet;
        inv[8] = (a * e - b * d) * invDet;
        return true;
    }

    /// <summary>
    /// Applies EXIF orientation correction (rotation/flip) to the processed image.
    /// Orientation values 1-8 follow the EXIF standard.
    /// </summary>
    public static ImageFrame ApplyOrientation(ImageFrame image, int orientation)
    {
        return orientation switch
        {
            2 => FlipHorizontal(image),
            3 => Rotate180(image),
            4 => FlipVertical(image),
            5 => Transpose(image),
            6 => Rotate90CW(image),
            7 => Transverse(image),
            8 => Rotate90CCW(image),
            _ => image // 1 (normal) or out-of-range
        };
    }

    /// <summary>EXIF orientation 2: mirror across the vertical axis.</summary>
    private static ImageFrame FlipHorizontal(ImageFrame image)
    {
        int channels = image.NumberOfChannels;
        long rows = image.Rows;
        long columns = image.Columns;

        for (long y = 0; y < rows; y++)
        {
            Span<ushort> row = image.GetPixelRowForWrite(y);

            for (long left = 0, right = columns - 1; left < right; left++, right--)
            {
                int lOff = (int)(left * channels);
                int rOff = (int)(right * channels);

                for (int c = 0; c < channels; c++)
                    (row[lOff + c], row[rOff + c]) = (row[rOff + c], row[lOff + c]);
            }
        }

        return image;
    }

    /// <summary>EXIF orientation 3: rotate 180°.</summary>
    private static ImageFrame Rotate180(ImageFrame image)
    {
        FlipHorizontal(image);
        FlipVertical(image);
        return image;
    }

    /// <summary>EXIF orientation 4: mirror across the horizontal axis.</summary>
    private static ImageFrame FlipVertical(ImageFrame image)
    {
        long rows = image.Rows;
        long columns = image.Columns;
        int channels = image.NumberOfChannels;
        int rowLength = (int)(columns * channels);

        // Heap-allocate the swap buffer — stackalloc is impractical for typical row sizes
        ushort[] tempBuffer = new ushort[rowLength];
        Span<ushort> temp = tempBuffer.AsSpan();

        for (long top = 0, bottom = rows - 1; top < bottom; top++, bottom--)
        {
            Span<ushort> topRow = image.GetPixelRowForWrite(top);
            Span<ushort> bottomRow = image.GetPixelRowForWrite(bottom);

            topRow[..rowLength].CopyTo(temp);
            bottomRow[..rowLength].CopyTo(topRow);
            temp[..rowLength].CopyTo(bottomRow);
        }

        return image;
    }

    /// <summary>EXIF orientation 5: transpose (reflect over main diagonal).</summary>
    private static ImageFrame Transpose(ImageFrame image)
    {
        long srcCols = image.Columns;
        long srcRows = image.Rows;
        int channels = image.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(srcRows, srcCols, image.Colorspace, image.HasAlpha);

        for (long sy = 0; sy < srcRows; sy++)
        {
            ReadOnlySpan<ushort> srcRow = image.GetPixelRow(sy);

            for (long sx = 0; sx < srcCols; sx++)
            {
                int srcOff = (int)(sx * channels);
                Span<ushort> dstRow = result.GetPixelRowForWrite(sx);
                int dstOff = (int)(sy * channels);

                for (int c = 0; c < channels; c++)
                    dstRow[dstOff + c] = srcRow[srcOff + c];
            }
        }

        return result;
    }

    /// <summary>EXIF orientation 6: rotate 90° clockwise.</summary>
    private static ImageFrame Rotate90CW(ImageFrame image)
    {
        long srcCols = image.Columns;
        long srcRows = image.Rows;
        int channels = image.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(srcRows, srcCols, image.Colorspace, image.HasAlpha);

        for (long sy = 0; sy < srcRows; sy++)
        {
            ReadOnlySpan<ushort> srcRow = image.GetPixelRow(sy);

            for (long sx = 0; sx < srcCols; sx++)
            {
                int srcOff = (int)(sx * channels);
                long dstX = srcRows - 1 - sy;
                long dstY = sx;
                Span<ushort> dstRow = result.GetPixelRowForWrite(dstY);
                int dstOff = (int)(dstX * channels);

                for (int c = 0; c < channels; c++)
                    dstRow[dstOff + c] = srcRow[srcOff + c];
            }
        }

        return result;
    }

    /// <summary>EXIF orientation 7: transverse (reflect over anti-diagonal).</summary>
    private static ImageFrame Transverse(ImageFrame image)
    {
        long srcCols = image.Columns;
        long srcRows = image.Rows;
        int channels = image.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(srcRows, srcCols, image.Colorspace, image.HasAlpha);

        for (long sy = 0; sy < srcRows; sy++)
        {
            ReadOnlySpan<ushort> srcRow = image.GetPixelRow(sy);

            for (long sx = 0; sx < srcCols; sx++)
            {
                int srcOff = (int)(sx * channels);
                long dstX = srcRows - 1 - sy;
                long dstY = srcCols - 1 - sx;
                Span<ushort> dstRow = result.GetPixelRowForWrite(dstY);
                int dstOff = (int)(dstX * channels);

                for (int c = 0; c < channels; c++)
                    dstRow[dstOff + c] = srcRow[srcOff + c];
            }
        }

        return result;
    }

    /// <summary>EXIF orientation 8: rotate 90° counter-clockwise.</summary>
    private static ImageFrame Rotate90CCW(ImageFrame image)
    {
        long srcCols = image.Columns;
        long srcRows = image.Rows;
        int channels = image.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(srcRows, srcCols, image.Colorspace, image.HasAlpha);

        for (long sy = 0; sy < srcRows; sy++)
        {
            ReadOnlySpan<ushort> srcRow = image.GetPixelRow(sy);

            for (long sx = 0; sx < srcCols; sx++)
            {
                int srcOff = (int)(sx * channels);
                long dstX = sy;
                long dstY = srcCols - 1 - sx;
                Span<ushort> dstRow = result.GetPixelRowForWrite(dstY);
                int dstOff = (int)(dstX * channels);

                for (int c = 0; c < channels; c++)
                    dstRow[dstOff + c] = srcRow[srcOff + c];
            }
        }

        return result;
    }

    /// <summary>
    /// Clamps a long value to the valid ushort range [0, 65535].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ClampToUshort(long value)
    {
        if (value <= 0) return 0;
        if (value >= 65535) return 65535;
        return (ushort)value;
    }
}
