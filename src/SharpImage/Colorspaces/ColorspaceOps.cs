using System.Runtime.CompilerServices;

namespace SharpImage.Colorspaces;

using SharpImage.Core;
using SharpImage.Image;

/// <summary>
/// Image-level colorspace conversion operations. Converts pixel data between sRGB and target
/// colorspaces, storing converted values in the RGB channels for visualization or processing.
/// </summary>
public static class ColorspaceOps
{
    /// <summary>
    /// Converts an image from sRGB to the specified colorspace. The converted component values
    /// are stored in the R, G, B channels (e.g., for Oklab: R=L, G=a, B=b).
    /// </summary>
    public static ImageFrame ConvertToColorspace(ImageFrame source, string targetColorspace)
    {
        var converter = GetForwardConverter(targetColorspace);

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                converter(srcRow[off], srcRow[off + 1], srcRow[off + 2],
                    out double c1, out double c2, out double c3);

                dstRow[off] = (ushort)Math.Clamp(c1 + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 1] = (ushort)Math.Clamp(c2 + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 2] = (ushort)Math.Clamp(c3 + 0.5, 0, Quantum.MaxValue);

                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    /// <summary>
    /// Converts an image from the specified colorspace back to sRGB. Assumes channel values
    /// were stored via ConvertToColorspace.
    /// </summary>
    public static ImageFrame ConvertFromColorspace(ImageFrame source, string sourceColorspace)
    {
        var converter = GetInverseConverter(sourceColorspace);

        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        var result = new ImageFrame();
        result.Initialize(width, height, source.Colorspace, source.HasAlpha);

        Parallel.For(0, height, y =>
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                converter(srcRow[off], srcRow[off + 1], srcRow[off + 2],
                    out double r, out double g, out double b);

                dstRow[off] = (ushort)Math.Clamp(r + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 1] = (ushort)Math.Clamp(g + 0.5, 0, Quantum.MaxValue);
                dstRow[off + 2] = (ushort)Math.Clamp(b + 0.5, 0, Quantum.MaxValue);

                if (channels > 3)
                    dstRow[off + 3] = srcRow[off + 3];
            }
        });

        return result;
    }

    /// <summary>
    /// Round-trips an image: sRGB → target colorspace → sRGB. Useful for verifying
    /// conversion accuracy and for gamut mapping.
    /// </summary>
    public static ImageFrame RoundTrip(ImageFrame source, string colorspace)
    {
        var converted = ConvertToColorspace(source, colorspace);
        return ConvertFromColorspace(converted, colorspace);
    }

    /// <summary>
    /// Returns a list of all supported colorspace names.
    /// </summary>
    public static IReadOnlyList<string> SupportedColorspaces { get; } =
    [
        "hsl", "hsv", "hsi", "hwb", "hcl", "hclp",
        "xyz", "lab", "lchab", "luv", "lchuv", "lms",
        "ypbpr", "ycbcr", "yiq", "yuv", "ydbdr",
        "oklab", "oklch", "jzazbz", "jzczhz",
        "displayp3", "prophoto"
    ];

    // Forward converters output quantum-scaled values for storage in image channels.
    // For colorspaces that output normalized [0,1] values, we scale to [0, QuantumMax].
    // For colorspaces that output quantum values, we pass through.

    private delegate void ForwardConverter(double r, double g, double b,
        out double c1, out double c2, out double c3);

    private delegate void InverseConverter(double c1, double c2, double c3,
        out double r, out double g, out double b);

    private static ForwardConverter GetForwardConverter(string colorspace)
    {
        return colorspace.ToLowerInvariant() switch
        {
            "hsl" => WrapNormalized(ColorspaceConverter.RgbToHsl),
            "hsv" => WrapNormalized(ColorspaceConverter.RgbToHsv),
            "hsi" => WrapNormalized(ColorspaceConverter.RgbToHsi),
            "hwb" => WrapNormalized(ColorspaceConverter.RgbToHwb),
            "hcl" => WrapNormalized(ColorspaceConverter.RgbToHcl),
            "hclp" => WrapNormalized(ColorspaceConverter.RgbToHclp),
            "xyz" => WrapNormalized(ColorspaceConverter.RgbToXyz),
            "lab" => WrapNormalized(ColorspaceConverter.RgbToLab),
            "lchab" => WrapNormalized(ColorspaceConverter.RgbToLchab),
            "luv" => WrapNormalized(ColorspaceConverter.RgbToLuv),
            "lchuv" => WrapNormalized(ColorspaceConverter.RgbToLchuv),
            "lms" => WrapNormalized(ColorspaceConverter.RgbToLms),
            "ypbpr" => WrapNormalized(ColorspaceConverter.RgbToYPbPr),
            "ycbcr" => WrapNormalized(ColorspaceConverter.RgbToYCbCr),
            "yiq" => WrapNormalized(ColorspaceConverter.RgbToYiq),
            "yuv" => WrapNormalized(ColorspaceConverter.RgbToYuv),
            "ydbdr" => WrapNormalized(ColorspaceConverter.RgbToYDbDr),
            "oklab" => WrapNormalized(ColorspaceConverter.RgbToOklab),
            "oklch" => WrapNormalized(ColorspaceConverter.RgbToOklch),
            "jzazbz" => WrapNormalized(ColorspaceConverter.RgbToJzazbz),
            "jzczhz" => WrapNormalized(ColorspaceConverter.RgbToJzczhz),
            "displayp3" => WrapQuantum(ColorspaceConverter.RgbToDisplayP3),
            "prophoto" => WrapQuantum(ColorspaceConverter.RgbToProPhoto),
            _ => throw new ArgumentException($"Unknown colorspace: {colorspace}. Supported: {string.Join(", ", SupportedColorspaces)}")
        };
    }

    private static InverseConverter GetInverseConverter(string colorspace)
    {
        return colorspace.ToLowerInvariant() switch
        {
            "hsl" => WrapFromNormalized(ColorspaceConverter.HslToRgb),
            "hsv" => WrapFromNormalized(ColorspaceConverter.HsvToRgb),
            "hsi" => WrapFromNormalized(ColorspaceConverter.HsiToRgb),
            "hwb" => WrapFromNormalized(ColorspaceConverter.HwbToRgb),
            "hcl" => WrapFromNormalized(ColorspaceConverter.HclToRgb),
            "hclp" => WrapFromNormalized(ColorspaceConverter.HclpToRgb),
            "xyz" => WrapFromNormalized(ColorspaceConverter.XyzToRgb),
            "lab" => WrapFromNormalized(ColorspaceConverter.LabToRgb),
            "lchab" => WrapFromNormalized(ColorspaceConverter.LchabToRgb),
            "luv" => WrapFromNormalized(ColorspaceConverter.LuvToRgb),
            "lchuv" => WrapFromNormalized(ColorspaceConverter.LchuvToRgb),
            "lms" => WrapFromNormalized(ColorspaceConverter.LmsToRgb),
            "ypbpr" => WrapFromNormalized(ColorspaceConverter.YPbPrToRgb),
            "ycbcr" => WrapFromNormalized(ColorspaceConverter.YCbCrToRgb),
            "yiq" => WrapFromNormalized(ColorspaceConverter.YiqToRgb),
            "yuv" => WrapFromNormalized(ColorspaceConverter.YuvToRgb),
            "ydbdr" => WrapFromNormalized(ColorspaceConverter.YDbDrToRgb),
            "oklab" => WrapFromNormalized(ColorspaceConverter.OklabToRgb),
            "oklch" => WrapFromNormalized(ColorspaceConverter.OklchToRgb),
            "jzazbz" => WrapFromNormalized(ColorspaceConverter.JzazbzToRgb),
            "jzczhz" => WrapFromNormalized(ColorspaceConverter.JzczhzToRgb),
            "displayp3" => WrapFromQuantum(ColorspaceConverter.DisplayP3ToRgb),
            "prophoto" => WrapFromQuantum(ColorspaceConverter.ProPhotoToRgb),
            _ => throw new ArgumentException($"Unknown colorspace: {colorspace}. Supported: {string.Join(", ", SupportedColorspaces)}")
        };
    }

    // Wraps a converter that takes quantum RGB and outputs normalized [0,1] values.
    // Scales output to [0, QuantumMax] for storage.
    private static ForwardConverter WrapNormalized(
        NormalizedForward fn)
    {
        return (double r, double g, double b, out double c1, out double c2, out double c3) =>
        {
            fn(r, g, b, out double n1, out double n2, out double n3);
            c1 = n1 * Quantum.MaxValue;
            c2 = n2 * Quantum.MaxValue;
            c3 = n3 * Quantum.MaxValue;
        };
    }

    // Wraps a converter that outputs quantum-scaled values (like DisplayP3, ProPhoto).
    private static ForwardConverter WrapQuantum(
        NormalizedForward fn)
    {
        return (double r, double g, double b, out double c1, out double c2, out double c3) =>
        {
            fn(r, g, b, out c1, out c2, out c3);
        };
    }

    // Wraps converters that output raw tristimulus values (like LMS, XYZ-like).
    // Clamps to [0, QuantumMax].
    private static ForwardConverter WrapXyzLike(
        NormalizedForward fn)
    {
        return (double r, double g, double b, out double c1, out double c2, out double c3) =>
        {
            fn(r, g, b, out double x1, out double x2, out double x3);
            c1 = Math.Clamp(x1, 0, 1) * Quantum.MaxValue;
            c2 = Math.Clamp(x2, 0, 1) * Quantum.MaxValue;
            c3 = Math.Clamp(x3, 0, 1) * Quantum.MaxValue;
        };
    }

    // Inverse: takes quantum-scaled channels, converts back to normalized, calls inverse converter.
    private static InverseConverter WrapFromNormalized(
        NormalizedForward fn)
    {
        return (double c1, double c2, double c3, out double r, out double g, out double b) =>
        {
            fn(c1 * Quantum.Scale, c2 * Quantum.Scale, c3 * Quantum.Scale,
                out r, out g, out b);
        };
    }

    // Inverse for quantum-output colorspaces (pass through).
    private static InverseConverter WrapFromQuantum(
        NormalizedForward fn)
    {
        return (double c1, double c2, double c3, out double r, out double g, out double b) =>
        {
            fn(c1, c2, c3, out r, out g, out b);
        };
    }

    // Inverse for XYZ-like: scale back from quantum to [0,1] raw values.
    private static InverseConverter WrapFromXyzLike(
        NormalizedForward fn)
    {
        return (double c1, double c2, double c3, out double r, out double g, out double b) =>
        {
            fn(c1 * Quantum.Scale, c2 * Quantum.Scale, c3 * Quantum.Scale,
                out r, out g, out b);
        };
    }

    private delegate void NormalizedForward(double a, double b, double c,
        out double o1, out double o2, out double o3);
}
