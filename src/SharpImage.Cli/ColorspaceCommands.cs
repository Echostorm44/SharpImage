using System.CommandLine;
using System.CommandLine.Parsing;

namespace SharpImage.Cli;

using SharpImage.Colorspaces;

public static class ColorspaceCommands
{
    public static Command CreateColorspaceConvertCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var targetOpt = new Option<string>("--to", "-t")
        {
            Description = "Target colorspace: oklab, oklch, jzazbz, jzczhz, displayp3, prophoto, hsl, hsv, lab, xyz, etc.",
            Required = true
        };
        var cmd = new Command("colorspace", "Convert image to a different colorspace. Channel values are stored in R/G/B for visualization.");
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(targetOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string target = parseResult.GetValue(targetOpt)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Colorspace → {target}",
                img => ColorspaceOps.ConvertToColorspace(img, target));
        });
        return cmd;
    }

    public static Command CreateColorspaceRoundtripCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var viaOpt = new Option<string>("--via", "-v")
        {
            Description = "Colorspace to round-trip through: oklab, oklch, jzazbz, jzczhz, displayp3, prophoto, etc.",
            Required = true
        };
        var cmd = new Command("colorspace-roundtrip", "Round-trip image through a colorspace (sRGB → target → sRGB) to verify conversion fidelity.");
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(viaOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string via = parseResult.GetValue(viaOpt)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Round-trip via {via}",
                img => ColorspaceOps.RoundTrip(img, via));
        });
        return cmd;
    }

    public static Command CreateListColorspacesCommand()
    {
        var cmd = new Command("colorspaces", "List all supported colorspaces.");
        cmd.SetAction((_) =>
        {
            Console.WriteLine("Supported colorspaces:");
            Console.WriteLine();
            Console.WriteLine("  Perceptual (modern):");
            Console.WriteLine("    oklab      - Oklab (Björn Ottosson, 2020) — perceptually uniform");
            Console.WriteLine("    oklch      - Oklch (cylindrical Oklab) — hue-preserving gradients");
            Console.WriteLine("    jzazbz     - JzAzBz (Safdar et al.) — HDR-aware perceptual");
            Console.WriteLine("    jzczhz     - JzCzhz (cylindrical JzAzBz) — HDR hue-preserving");
            Console.WriteLine();
            Console.WriteLine("  Wide gamut:");
            Console.WriteLine("    displayp3  - Display P3 (Apple) — 25% wider than sRGB");
            Console.WriteLine("    prophoto   - ProPhoto RGB (ROMM) — covers ~100% surface colors");
            Console.WriteLine();
            Console.WriteLine("  CIE standards:");
            Console.WriteLine("    xyz        - CIE 1931 XYZ tristimulus");
            Console.WriteLine("    lab        - CIE L*a*b* (perceptual, D65)");
            Console.WriteLine("    lchab      - CIE LCH (cylindrical Lab)");
            Console.WriteLine("    luv        - CIE L*u*v*");
            Console.WriteLine("    lchuv      - CIE LCH (cylindrical Luv)");
            Console.WriteLine("    lms        - LMS cone response (Bradford)");
            Console.WriteLine();
            Console.WriteLine("  Hue-based:");
            Console.WriteLine("    hsl        - Hue / Saturation / Lightness");
            Console.WriteLine("    hsv        - Hue / Saturation / Value");
            Console.WriteLine("    hsi        - Hue / Saturation / Intensity");
            Console.WriteLine("    hwb        - Hue / Whiteness / Blackness");
            Console.WriteLine("    hcl        - Hue / Chroma / Luminance");
            Console.WriteLine("    hclp       - HCL (polar Lab approximation)");
            Console.WriteLine();
            Console.WriteLine("  Video / broadcast:");
            Console.WriteLine("    ycbcr      - Y′CbCr (digital video)");
            Console.WriteLine("    ypbpr      - Y′PbPr (analog video)");
            Console.WriteLine("    yiq        - YIQ (NTSC)");
            Console.WriteLine("    yuv        - YUV (PAL/SECAM)");
            Console.WriteLine("    ydbdr      - YDbDr (SECAM)");
        });
        return cmd;
    }
}
