using System.Runtime.CompilerServices;

namespace SharpImage.Colorspaces;

using SharpImage.Core;

/// <summary>
/// Converts between sRGB and all supported colorspaces. All methods operate on quantum-scaled values [0, QuantumRange]
/// unless noted otherwise. Ported from ImageMagick MagickCore/colorspace-private.h and MagickCore/gem.c.
/// </summary>
public static class ColorspaceConverter
{
    private const double Epsilon = 1.0e-12;
    private const double CIEEpsilon = 216.0 / 24389.0;     // 0.008856...
    private const double CIEK = 24389.0 / 27.0;            // 903.296...

    // D65 illuminant tristimulus values
    private const double D65X = 0.95047;
    private const double D65Y = 1.00000;
    private const double D65Z = 1.08883;

    // --- HSL ---

    public static void RgbToHsl(double red, double green, double blue,
        out double hue, out double saturation, out double lightness)
    {
        double r = red * Quantum.Scale;
        double g = green * Quantum.Scale;
        double b = blue * Quantum.Scale;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double c = max - min;

        lightness = (max + min) / 2.0;

        if (c <= 0.0)
        {
            hue = 0.0;
            saturation = 0.0;
            return;
        }

        if (Math.Abs(max - r) < Epsilon)
        {
            hue = (g - b) / c;
            if (g < b)
            {
                hue += 6.0;
            }
        }
        else if (Math.Abs(max - g) < Epsilon)
        {
            hue = 2.0 + (b - r) / c;
        }
        else
        {
            hue = 4.0 + (r - g) / c;
        }

        hue *= 60.0 / 360.0;
        saturation = lightness <= 0.5
            ? c / (2.0 * lightness)
            : c / (2.0 - 2.0 * lightness);
    }

    public static void HslToRgb(double hue, double saturation, double lightness,
        out double red, out double green, out double blue)
    {
        double h = hue * 360.0;
        double c = lightness <= 0.5
            ? 2.0 * lightness * saturation
            : (2.0 - 2.0 * lightness) * saturation;
        double min = lightness - 0.5 * c;

        h -= 360.0 * Math.Floor(h / 360.0);
        h /= 60.0;
        double x = c * (1.0 - Math.Abs(h - 2.0 * Math.Floor(h / 2.0) - 1.0));

        double r, g, b;
        switch ((int)Math.Floor(h))
        {
            case 0: default:
                r = c;
                g = x;
                b = 0;
                break;
            case 1:
                r = x;
                g = c;
                b = 0;
                break;
            case 2:
                r = 0;
                g = c;
                b = x;
                break;
            case 3:
                r = 0;
                g = x;
                b = c;
                break;
            case 4:
                r = x;
                g = 0;
                b = c;
                break;
            case 5:
                r = c;
                g = 0;
                b = x;
                break;
        }

        red = Quantum.MaxValue * (min + r);
        green = Quantum.MaxValue * (min + g);
        blue = Quantum.MaxValue * (min + b);
    }

    // --- HSV ---

    public static void RgbToHsv(double red, double green, double blue,
        out double hue, out double saturation, out double value)
    {
        double r = red * Quantum.Scale;
        double g = green * Quantum.Scale;
        double b = blue * Quantum.Scale;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double c = max - min;

        value = max;

        if (c <= 0.0)
        {
            hue = 0.0;
            saturation = 0.0;
            return;
        }

        if (Math.Abs(max - r) < Epsilon)
        {
            hue = (g - b) / c;
            if (g < b)
            {
                hue += 6.0;
            }
        }
        else if (Math.Abs(max - g) < Epsilon)
        {
            hue = 2.0 + (b - r) / c;
        }
        else
        {
            hue = 4.0 + (r - g) / c;
        }

        hue *= 60.0 / 360.0;
        saturation = max > 0.0 ? c / max : 0.0;
    }

    public static void HsvToRgb(double hue, double saturation, double value,
        out double red, out double green, out double blue)
    {
        double h = hue * 360.0;
        double c = value * saturation;
        double min = value - c;

        h -= 360.0 * Math.Floor(h / 360.0);
        h /= 60.0;
        double x = c * (1.0 - Math.Abs(h - 2.0 * Math.Floor(h / 2.0) - 1.0));

        double r, g, b;
        switch ((int)Math.Floor(h))
        {
            case 0: default:
                r = c;
                g = x;
                b = 0;
                break;
            case 1:
                r = x;
                g = c;
                b = 0;
                break;
            case 2:
                r = 0;
                g = c;
                b = x;
                break;
            case 3:
                r = 0;
                g = x;
                b = c;
                break;
            case 4:
                r = x;
                g = 0;
                b = c;
                break;
            case 5:
                r = c;
                g = 0;
                b = x;
                break;
        }

        red = Quantum.MaxValue * (min + r);
        green = Quantum.MaxValue * (min + g);
        blue = Quantum.MaxValue * (min + b);
    }

    // --- HSI ---

    public static void RgbToHsi(double red, double green, double blue,
        out double hue, out double saturation, out double intensity)
    {
        double r = red * Quantum.Scale;
        double g = green * Quantum.Scale;
        double b = blue * Quantum.Scale;

        intensity = (r + g + b) / 3.0;

        if (intensity <= 0.0)
        {
            hue = 0.0;
            saturation = 0.0;
            return;
        }

        saturation = 1.0 - Math.Min(r, Math.Min(g, b)) / intensity;
        double alpha = 0.5 * (2.0 * r - g - b);
        double beta = 0.8660254037844385 * (g - b);  // sqrt(3)/2
        hue = Math.Atan2(beta, alpha) * (180.0 / Math.PI) / 360.0;
        if (hue < 0.0)
        {
            hue += 1.0;
        }
    }

    public static void HsiToRgb(double hue, double saturation, double intensity,
        out double red, out double green, out double blue)
    {
        double h = 360.0 * hue;
        h -= 360.0 * Math.Floor(h / 360.0);

        double r, g, b;
        if (h < 120.0)
        {
            b = intensity * (1.0 - saturation);
            r = intensity * (1.0 + saturation * Math.Cos(h * (Math.PI / 180.0)) /
                Math.Cos((60.0 - h) * (Math.PI / 180.0)));
            g = 3.0 * intensity - r - b;
        }
        else if (h < 240.0)
        {
            h -= 120.0;
            r = intensity * (1.0 - saturation);
            g = intensity * (1.0 + saturation * Math.Cos(h * (Math.PI / 180.0)) /
                Math.Cos((60.0 - h) * (Math.PI / 180.0)));
            b = 3.0 * intensity - r - g;
        }
        else
        {
            h -= 240.0;
            g = intensity * (1.0 - saturation);
            b = intensity * (1.0 + saturation * Math.Cos(h * (Math.PI / 180.0)) /
                Math.Cos((60.0 - h) * (Math.PI / 180.0)));
            r = 3.0 * intensity - g - b;
        }

        red = Quantum.MaxValue * r;
        green = Quantum.MaxValue * g;
        blue = Quantum.MaxValue * b;
    }

    // --- HWB ---

    public static void RgbToHwb(double red, double green, double blue,
        out double hue, out double whiteness, out double blackness)
    {
        double w = Math.Min(red, Math.Min(green, blue));
        double v = Math.Max(red, Math.Max(green, blue));

        blackness = 1.0 - Quantum.Scale * v;
        whiteness = Quantum.Scale * w;

        if (Math.Abs(v - w) < Epsilon)
        {
            hue = -1.0;
            return;
        }

        double f = Math.Abs(red - w) < Epsilon ? green - blue
                 : Math.Abs(green - w) < Epsilon ? blue - red
                 : red - green;
        double p = Math.Abs(red - w) < Epsilon ? 3.0
                 : Math.Abs(green - w) < Epsilon ? 5.0
                 : 1.0;
        hue = (p - f / (v - w)) / 6.0;
    }

    public static void HwbToRgb(double hue, double whiteness, double blackness,
        out double red, out double green, out double blue)
    {
        double v = 1.0 - blackness;

        if (Math.Abs(hue - (-1.0)) < Epsilon)
        {
            red = green = blue = Quantum.MaxValue * v;
            return;
        }

        int i = (int)Math.Floor(6.0 * hue);
        double f = 6.0 * hue - i;
        if ((i & 0x01) != 0)
        {
            f = 1.0 - f;
        }

        double n = whiteness + f * (v - whiteness);

        double r, g, b;
        switch (i)
        {
            case 0: default:
                r = v;
                g = n;
                b = whiteness;
                break;
            case 1:
                r = n;
                g = v;
                b = whiteness;
                break;
            case 2:
                r = whiteness;
                g = v;
                b = n;
                break;
            case 3:
                r = whiteness;
                g = n;
                b = v;
                break;
            case 4:
                r = n;
                g = whiteness;
                b = v;
                break;
            case 5:
                r = v;
                g = whiteness;
                b = n;
                break;
        }

        red = Quantum.MaxValue * r;
        green = Quantum.MaxValue * g;
        blue = Quantum.MaxValue * b;
    }

    // --- HCL ---

    public static void RgbToHcl(double red, double green, double blue,
        out double hue, out double chroma, out double luma)
    {
        double max = Math.Max(red, Math.Max(green, blue));
        double c = max - Math.Min(red, Math.Min(green, blue));

        double h = 0.0;
        if (c >= Epsilon)
        {
            if (Math.Abs(red - max) < Epsilon)
            {
                h = ((green - blue) / c + 6.0) % 6.0;
            }
            else if (Math.Abs(green - max) < Epsilon)
            {
                h = (blue - red) / c + 2.0;
            }
            else
            {
                h = (red - green) / c + 4.0;
            }
        }

        hue = h / 6.0;
        chroma = Quantum.Scale * c;
        luma = Quantum.Scale * (0.298839 * red + 0.586811 * green + 0.114350 * blue);
    }

    public static void HclToRgb(double hue, double chroma, double luma,
        out double red, out double green, out double blue)
    {
        double h = 6.0 * hue;
        double c = chroma;
        double x = c * (1.0 - Math.Abs(h % 2.0 - 1.0));

        double r = 0, g = 0, b = 0;
        if (h >= 0 && h < 1)
        {
            r = c;
            g = x;
        }
        else if (h >= 1 && h < 2)
        {
            r = x;
            g = c;
        }
        else if (h >= 2 && h < 3)
        {
            g = c;
            b = x;
        }
        else if (h >= 3 && h < 4)
        {
            g = x;
            b = c;
        }
        else if (h >= 4 && h < 5)
        {
            r = x;
            b = c;
        }
        else if (h >= 5 && h < 6)
        {
            r = c;
            b = x;
        }

        double m = luma - (0.298839 * r + 0.586811 * g + 0.114350 * b);

        red = Quantum.MaxValue * (r + m);
        green = Quantum.MaxValue * (g + m);
        blue = Quantum.MaxValue * (b + m);
    }

    // --- HCLp (HCL with gamut clamping) ---

    public static void RgbToHclp(double red, double green, double blue,
        out double hue, out double chroma, out double luma)
    {
        // Same as HCL for the forward direction
        RgbToHcl(red, green, blue, out hue, out chroma, out luma);
    }

    public static void HclpToRgb(double hue, double chroma, double luma,
        out double red, out double green, out double blue)
    {
        double h = 6.0 * hue;
        double c = chroma;
        double x = c * (1.0 - Math.Abs(h % 2.0 - 1.0));

        double r = 0, g = 0, b = 0;
        if (h >= 0 && h < 1)
        {
            r = c;
            g = x;
        }
        else if (h >= 1 && h < 2)
        {
            r = x;
            g = c;
        }
        else if (h >= 2 && h < 3)
        {
            g = c;
            b = x;
        }
        else if (h >= 3 && h < 4)
        {
            g = x;
            b = c;
        }
        else if (h >= 4 && h < 5)
        {
            r = x;
            b = c;
        }
        else if (h >= 5 && h < 6)
        {
            r = c;
            b = x;
        }

        double m = luma - (0.298839 * r + 0.586811 * g + 0.114350 * b);

        // Gamut clamping
        double z = 1.0;
        if (m < 0.0)
        {
            z = luma / (luma - m);
            m = 0.0;
        }
        else if (m + c > 1.0)
        {
            z = (1.0 - luma) / (m + c - luma);
            m = 1.0 - z * c;
        }

        red = Quantum.MaxValue * (z * r + m);
        green = Quantum.MaxValue * (z * g + m);
        blue = Quantum.MaxValue * (z * b + m);
    }

    // --- XYZ (CIE 1931, D65 illuminant) ---

    public static void RgbToXyz(double red, double green, double blue,
        out double x, out double y, out double z)
    {
        double r = Quantum.Scale * SrgbConverter.Decode(red);
        double g = Quantum.Scale * SrgbConverter.Decode(green);
        double b = Quantum.Scale * SrgbConverter.Decode(blue);

        x = 0.4123955889674142161 * r + 0.3575834307637148171 * g + 0.1804926473817015735 * b;
        y = 0.2125862307855955516 * r + 0.7151703037034108499 * g + 0.07220049864333622685 * b;
        z = 0.01929721549174694484 * r + 0.1191838645808485318 * g + 0.9504971251315797660 * b;
    }

    public static void XyzToRgb(double x, double y, double z,
        out double red, out double green, out double blue)
    {
        double r = 3.240969941904521 * x + (-1.537383177570093) * y + (-0.498610760293) * z;
        double g = (-0.96924363628087) * x + 1.87596750150772 * y + 0.041555057407175 * z;
        double b = 0.055630079696993 * x + (-0.20397695888897) * y + 1.056971514242878 * z;

        double min = Math.Min(r, Math.Min(g, b));
        if (min < 0.0)
        {
            r -= min;
            g -= min;
            b -= min;
        }

        red = SrgbConverter.Encode(Quantum.MaxValue * r);
        green = SrgbConverter.Encode(Quantum.MaxValue * g);
        blue = SrgbConverter.Encode(Quantum.MaxValue * b);
    }

    // --- Lab (CIE L*a*b*, D65) ---

    public static void RgbToLab(double red, double green, double blue,
        out double l, out double a, out double b)
    {
        RgbToXyz(red, green, blue, out double x, out double y, out double z);
        XyzToLab(x, y, z, out l, out a, out b);
    }

    public static void LabToRgb(double l, double a, double b,
        out double red, out double green, out double blue)
    {
        LabToXyz(100.0 * l, 255.0 * (a - 0.5), 255.0 * (b - 0.5),
            out double x, out double y, out double z);
        XyzToRgb(x, y, z, out red, out green, out blue);
    }

    private static void XyzToLab(double X, double Y, double Z,
        out double l, out double a, out double b)
    {
        double fx = X / D65X > CIEEpsilon ? Math.Cbrt(X / D65X) : (CIEK * X / D65X + 16.0) / 116.0;
        double fy = Y / D65Y > CIEEpsilon ? Math.Cbrt(Y / D65Y) : (CIEK * Y / D65Y + 16.0) / 116.0;
        double fz = Z / D65Z > CIEEpsilon ? Math.Cbrt(Z / D65Z) : (CIEK * Z / D65Z + 16.0) / 116.0;

        l = (116.0 * fy - 16.0) / 100.0;
        a = 500.0 * (fx - fy) / 255.0 + 0.5;
        b = 200.0 * (fy - fz) / 255.0 + 0.5;
    }

    private static void LabToXyz(double L, double a, double b,
        out double X, out double Y, out double Z)
    {
        double fy = (L + 16.0) / 116.0;
        double fx = fy + a / 500.0;
        double fz = fy - b / 200.0;

        X = fx * fx * fx > CIEEpsilon ? fx * fx * fx : (116.0 * fx - 16.0) / CIEK;
        Y = L > CIEK * CIEEpsilon ? fy * fy * fy : L / CIEK;
        Z = fz * fz * fz > CIEEpsilon ? fz * fz * fz : (116.0 * fz - 16.0) / CIEK;

        X *= D65X;
        Y *= D65Y;
        Z *= D65Z;
    }

    // --- LCHab (cylindrical Lab) ---

    public static void RgbToLchab(double red, double green, double blue,
        out double luma, out double chroma, out double hue)
    {
        RgbToXyz(red, green, blue, out double x, out double y, out double z);
        XyzToLab(x, y, z, out double l, out double a, out double b);

        luma = l;
        chroma = Math.Sqrt((a - 0.5) * (a - 0.5) + (b - 0.5) * (b - 0.5)) + 0.5;
        hue = 180.0 * Math.Atan2(b - 0.5, a - 0.5) / Math.PI / 360.0;
        if (hue < 0.0)
        {
            hue += 1.0;
        }
    }

    public static void LchabToRgb(double luma, double chroma, double hue,
        out double red, out double green, out double blue)
    {
        double c = 255.0 * (chroma - 0.5);
        double h = 360.0 * hue;
        double a = c * Math.Cos(h * Math.PI / 180.0);
        double b = c * Math.Sin(h * Math.PI / 180.0);
        LabToXyz(100.0 * luma, a, b, out double x, out double y, out double z);
        XyzToRgb(x, y, z, out red, out green, out blue);
    }

    // --- Luv (CIE L*u*v*) ---

    public static void RgbToLuv(double red, double green, double blue,
        out double l, out double u, out double v)
    {
        RgbToXyz(red, green, blue, out double x, out double y, out double z);
        XyzToLuv(x, y, z, out l, out u, out v);
    }

    public static void LuvToRgb(double l, double u, double v,
        out double red, out double green, out double blue)
    {
        LuvToXyz(100.0 * l, 354.0 * u - 134.0, 262.0 * v - 140.0,
            out double x, out double y, out double z);
        XyzToRgb(x, y, z, out red, out green, out blue);
    }

    private static void XyzToLuv(double X, double Y, double Z,
        out double l, out double u, out double v)
    {
        double yr = Y / D65Y;
        l = yr > CIEEpsilon ? 116.0 * Math.Cbrt(yr) - 16.0 : CIEK * yr;

        double denom = X + 15.0 * Y + 3.0 * Z;
        double alpha = denom > Epsilon ? 1.0 / denom : 0.0;

        double refDenom = D65X + 15.0 * D65Y + 3.0 * D65Z;
        double uRef = 4.0 * D65X / refDenom;
        double vRef = 9.0 * D65Y / refDenom;

        u = 13.0 * l * (4.0 * alpha * X - uRef);
        v = 13.0 * l * (9.0 * alpha * Y - vRef);

        l /= 100.0;
        u = (u + 134.0) / 354.0;
        v = (v + 140.0) / 262.0;
    }

    private static void LuvToXyz(double L, double u, double v,
        out double X, out double Y, out double Z)
    {
        Y = L > CIEK * CIEEpsilon ? Math.Pow((L + 16.0) / 116.0, 3.0) : L / CIEK;

        double refDenom = D65X + 15.0 * D65Y + 3.0 * D65Z;
        double u0 = 4.0 * D65X / refDenom;
        double v0 = 9.0 * D65Y / refDenom;

        double a = (52.0 * L / (u + 13.0 * L * u0) - 1.0) / 3.0;
        double b2 = -5.0 * Y;
        double d = Y * (39.0 * L / (v + 13.0 * L * v0) - 5.0);

        X = (d - b2) / (a + 1.0 / 3.0);
        Z = X * a + b2;

        // Clamp negatives
        if (X < 0.0)
        {
            X = 0.0;
        }

        if (Y < 0.0)
        {
            Y = 0.0;
        }

        if (Z < 0.0)
        {
            Z = 0.0;
        }
    }

    // --- LCHuv (cylindrical Luv) ---

    public static void RgbToLchuv(double red, double green, double blue,
        out double luma, out double chroma, out double hue)
    {
        RgbToLuv(red, green, blue, out double l, out double u, out double v);

        luma = l;
        double uRaw = 354.0 * u - 134.0;
        double vRaw = 262.0 * v - 140.0;
        // Max sRGB chroma ~224; divisor of 500 ensures [0, 1] range with headroom
        chroma = Math.Sqrt(uRaw * uRaw + vRaw * vRaw) / 500.0 + 0.5;
        hue = 180.0 * Math.Atan2(vRaw, uRaw) / Math.PI / 360.0;
        if (hue < 0.0)
        {
            hue += 1.0;
        }
    }

    public static void LchuvToRgb(double luma, double chroma, double hue,
        out double red, out double green, out double blue)
    {
        double c = 500.0 * (chroma - 0.5);
        double h = 360.0 * hue;
        double u = c * Math.Cos(h * Math.PI / 180.0);
        double v = c * Math.Sin(h * Math.PI / 180.0);
        LuvToXyz(100.0 * luma, u, v, out double x, out double y, out double z);
        XyzToRgb(x, y, z, out red, out green, out blue);
    }

    // --- YCbCr / YPbPr ---

    public static void RgbToYPbPr(double red, double green, double blue,
        out double y, out double pb, out double pr)
    {
        y = Quantum.Scale * (0.298839 * red + 0.586811 * green + 0.114350 * blue);
        pb = Quantum.Scale * (-0.1687367 * red - 0.331264 * green + 0.5 * blue) + 0.5;
        pr = Quantum.Scale * (0.5 * red - 0.418688 * green - 0.081312 * blue) + 0.5;
    }

    public static void YPbPrToRgb(double y, double pb, double pr,
        out double red, out double green, out double blue)
    {
        red = Quantum.MaxValue * (0.99999999999914679361 * y -
            1.2188941887145875e-06 * (pb - 0.5) + 1.4019995886561440468 * (pr - 0.5));
        green = Quantum.MaxValue * (0.99999975910502514331 * y -
            0.34413567816504303521 * (pb - 0.5) - 0.71413649331646789076 * (pr - 0.5));
        blue = Quantum.MaxValue * (1.00000124040004623180 * y +
            1.77200006607230409200 * (pb - 0.5) + 2.1453384174593273e-06 * (pr - 0.5));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RgbToYCbCr(double red, double green, double blue,
        out double y, out double cb, out double cr) =>
        RgbToYPbPr(red, green, blue, out y, out cb, out cr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void YCbCrToRgb(double y, double cb, double cr,
        out double red, out double green, out double blue) =>
        YPbPrToRgb(y, cb, cr, out red, out green, out blue);

    // --- YIQ ---

    public static void RgbToYiq(double red, double green, double blue,
        out double y, out double i, out double q)
    {
        y = Quantum.Scale * (0.298839 * red + 0.586811 * green + 0.114350 * blue);
        i = Quantum.Scale * (0.595716 * red - 0.274453 * green - 0.321263 * blue) + 0.5;
        q = Quantum.Scale * (0.211456 * red - 0.522591 * green + 0.311135 * blue) + 0.5;
    }

    public static void YiqToRgb(double y, double i, double q,
        out double red, out double green, out double blue)
    {
        red = Quantum.MaxValue * (y + 0.9562957197589482261 * (i - 0.5) +
            0.6210244164652610754 * (q - 0.5));
        green = Quantum.MaxValue * (y - 0.2721220993185104464 * (i - 0.5) -
            0.6473805968256950427 * (q - 0.5));
        blue = Quantum.MaxValue * (y - 1.1069890167364901945 * (i - 0.5) +
            1.7046149983646481374 * (q - 0.5));
    }

    // --- YUV ---

    public static void RgbToYuv(double red, double green, double blue,
        out double y, out double u, out double v)
    {
        y = Quantum.Scale * (0.298839 * red + 0.586811 * green + 0.114350 * blue);
        u = Quantum.Scale * (-0.147 * red - 0.289 * green + 0.436 * blue) + 0.5;
        v = Quantum.Scale * (0.615 * red - 0.515 * green - 0.100 * blue) + 0.5;
    }

    public static void YuvToRgb(double y, double u, double v,
        out double red, out double green, out double blue)
    {
        red = Quantum.MaxValue * (y - 3.945707070708279e-05 * (u - 0.5) +
            1.1398279671717170825 * (v - 0.5));
        green = Quantum.MaxValue * (y - 0.3946101641414141437 * (u - 0.5) -
            0.5805003156565656797 * (v - 0.5));
        blue = Quantum.MaxValue * (y + 2.0319996843434342537 * (u - 0.5) -
            4.813762626262513e-04 * (v - 0.5));
    }

    // --- YDbDr ---

    // Db/Dr raw range is [-1.333, 1.333]*MaxValue; scale by 1/(2.666*MaxValue) to fit [0,1]
    private const double YdbdrChromaScale = 2.666;

    public static void RgbToYDbDr(double red, double green, double blue,
        out double y, out double db, out double dr)
    {
        y = Quantum.Scale * (0.298839 * red + 0.586811 * green + 0.114350 * blue);
        db = (-0.450 * red - 0.883 * green + 1.333 * blue) / (YdbdrChromaScale * Quantum.MaxValue) + 0.5;
        dr = (-1.333 * red + 1.116 * green + 0.217 * blue) / (YdbdrChromaScale * Quantum.MaxValue) + 0.5;
    }

    public static void YDbDrToRgb(double y, double db, double dr,
        out double red, out double green, out double blue)
    {
        double dbScaled = (db - 0.5) * YdbdrChromaScale;
        double drScaled = (dr - 0.5) * YdbdrChromaScale;
        red = Quantum.MaxValue * (y - 1.647252118369881e-04 * dbScaled -
            5.259466419014803e-01 * drScaled);
        green = Quantum.MaxValue * (y - 1.293899278184939e-01 * dbScaled +
            2.678653169679837e-01 * drScaled);
        blue = Quantum.MaxValue * (y + 6.644220310509702e-01 * dbScaled -
            1.132137831481925e-04 * drScaled);
    }

    // --- CMYK ---

    public static void RgbToCmyk(double red, double green, double blue,
        out double cyan, out double magenta, out double yellow, out double black)
    {
        double r = red * Quantum.Scale;
        double g = green * Quantum.Scale;
        double b = blue * Quantum.Scale;

        double k = 1.0 - Math.Max(r, Math.Max(g, b));
        if (k >= 1.0)
        {
            cyan = magenta = yellow = 0;
            black = Quantum.MaxValue;
            return;
        }

        double invK = 1.0 / (1.0 - k);
        cyan = Quantum.MaxValue * ((1.0 - r - k) * invK);
        magenta = Quantum.MaxValue * ((1.0 - g - k) * invK);
        yellow = Quantum.MaxValue * ((1.0 - b - k) * invK);
        black = Quantum.MaxValue * k;
    }

    public static void CmykToRgb(double cyan, double magenta, double yellow, double black,
        out double red, out double green, out double blue)
    {
        double c = cyan * Quantum.Scale;
        double m = magenta * Quantum.Scale;
        double y = yellow * Quantum.Scale;
        double k = black * Quantum.Scale;

        red = Quantum.MaxValue * (1.0 - c) * (1.0 - k);
        green = Quantum.MaxValue * (1.0 - m) * (1.0 - k);
        blue = Quantum.MaxValue * (1.0 - y) * (1.0 - k);
    }

    // --- LMS ---

    public static void RgbToLms(double red, double green, double blue,
        out double l, out double m, out double s)
    {
        RgbToXyz(red, green, blue, out double x, out double y, out double z);

        // Bradford chromatic adaptation matrix (D65 to CAT02 LMS)
        l = 0.7328 * x + 0.4296 * y - 0.1624 * z;
        m = -0.7036 * x + 1.6975 * y + 0.0061 * z;
        s = 0.0030 * x + 0.0136 * y + 0.9834 * z;
    }

    public static void LmsToRgb(double l, double m, double s,
        out double red, out double green, out double blue)
    {
        // Inverse Bradford (from ImageMagick ConvertLMSToXYZ)
        double x = 1.096123820835514 * l - 0.278869000218287 * m + 0.182745179382773 * s;
        double y = 0.454369041975359 * l + 0.473533154307412 * m + 0.072097803717229 * s;
        double z = -0.009627608738429 * l - 0.005698031216113 * m + 1.015325639954543 * s;

        XyzToRgb(x, y, z, out red, out green, out blue);
    }

    // --- Oklab (Björn Ottosson, 2020) ---
    // Perceptually uniform colorspace, superior to CIE Lab for gradients and gamut mapping.
    // Path: sRGB → linear RGB → LMS (M1) → cube root → Oklab (M2)

    public static void RgbToOklab(double red, double green, double blue,
        out double lightness, out double a, out double b)
    {
        double r = SrgbConverter.DecodeNormalized(red * Quantum.Scale);
        double g = SrgbConverter.DecodeNormalized(green * Quantum.Scale);
        double bl = SrgbConverter.DecodeNormalized(blue * Quantum.Scale);

        // M1: linear sRGB → LMS
        double lRaw = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * bl;
        double mRaw = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * bl;
        double sRaw = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * bl;

        // Cube root
        double lCbrt = Math.Cbrt(lRaw);
        double mCbrt = Math.Cbrt(mRaw);
        double sCbrt = Math.Cbrt(sRaw);

        // M2: cube-root LMS → Oklab
        lightness = 0.2104542553 * lCbrt + 0.7936177850 * mCbrt - 0.0040720468 * sCbrt;
        a = 1.9779984951 * lCbrt - 2.4285922050 * mCbrt + 0.4505937099 * sCbrt;
        b = 0.0259040371 * lCbrt + 0.7827717662 * mCbrt - 0.8086757660 * sCbrt;

        // Normalize: L is [0,1], a/b are ~[-0.4, 0.4] → shift to [0,1]
        a = a * 0.5 + 0.5;
        b = b * 0.5 + 0.5;
    }

    public static void OklabToRgb(double lightness, double a, double b,
        out double red, out double green, out double blue)
    {
        // Denormalize a/b from [0,1] to ~[-0.4, 0.4]
        a = (a - 0.5) * 2.0;
        b = (b - 0.5) * 2.0;

        // Inverse M2: Oklab → cube-root LMS
        double lCbrt = lightness + 0.3963377774 * a + 0.2158037573 * b;
        double mCbrt = lightness - 0.1055613458 * a - 0.0638541728 * b;
        double sCbrt = lightness - 0.0894841775 * a - 1.2914855480 * b;

        // Cube to get LMS
        double lRaw = lCbrt * lCbrt * lCbrt;
        double mRaw = mCbrt * mCbrt * mCbrt;
        double sRaw = sCbrt * sCbrt * sCbrt;

        // Inverse M1: LMS → linear sRGB
        double r = +4.0767416621 * lRaw - 3.3077115913 * mRaw + 0.2309699292 * sRaw;
        double g = -1.2684380046 * lRaw + 2.6097574011 * mRaw - 0.3413193965 * sRaw;
        double bl = -0.0041960863 * lRaw - 0.7034186147 * mRaw + 1.7076147010 * sRaw;

        // Clamp and encode sRGB gamma
        r = Math.Clamp(r, 0.0, 1.0);
        g = Math.Clamp(g, 0.0, 1.0);
        bl = Math.Clamp(bl, 0.0, 1.0);

        red = Quantum.MaxValue * SrgbConverter.EncodeNormalized(r);
        green = Quantum.MaxValue * SrgbConverter.EncodeNormalized(g);
        blue = Quantum.MaxValue * SrgbConverter.EncodeNormalized(bl);
    }

    // --- Oklch (cylindrical Oklab) ---

    public static void RgbToOklch(double red, double green, double blue,
        out double lightness, out double chroma, out double hue)
    {
        RgbToOklab(red, green, blue, out double l, out double a, out double b);

        lightness = l;

        // Denormalize a/b for polar conversion
        double aRaw = (a - 0.5) * 2.0;
        double bRaw = (b - 0.5) * 2.0;

        chroma = Math.Sqrt(aRaw * aRaw + bRaw * bRaw);
        hue = Math.Atan2(bRaw, aRaw) * (180.0 / Math.PI) / 360.0;
        if (hue < 0.0)
            hue += 1.0;
    }

    public static void OklchToRgb(double lightness, double chroma, double hue,
        out double red, out double green, out double blue)
    {
        double hRad = hue * 360.0 * (Math.PI / 180.0);
        double aRaw = chroma * Math.Cos(hRad);
        double bRaw = chroma * Math.Sin(hRad);

        // Normalize for OklabToRgb
        double a = aRaw * 0.5 + 0.5;
        double b = bRaw * 0.5 + 0.5;

        OklabToRgb(lightness, a, b, out red, out green, out blue);
    }

    // --- JzAzBz (Safdar et al., 2017) ---
    // HDR-aware perceptually uniform colorspace using the PQ transfer function.
    // Path: sRGB → linear RGB → XYZ (absolute) → modified XYZ → LMS → PQ → JzAzBz

    private const double JzB = 1.15;
    private const double JzG = 0.66;
    private const double JzD = -0.56;
    private const double JzD0 = 1.6295499532821566e-11;

    // PQ (Perceptual Quantizer) constants from BT.2100
    private const double PqC1 = 0.8359375;         // 3424/4096
    private const double PqC2 = 18.8515625;        // 2413/128
    private const double PqC3 = 18.6875;           // 2392/128
    private const double PqN = 0.15930175781;      // 2610/16384
    private const double PqP = 134.034375;         // 1.7 * 2523/32
    private const double SdrWhiteLuminance = 203.0; // SDR white in nits

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double PqForward(double x)
    {
        if (x <= 0.0) return 0.0;
        double xn = Math.Pow(x / 10000.0, PqN);
        return Math.Pow((PqC1 + PqC2 * xn) / (1.0 + PqC3 * xn), PqP);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double PqInverse(double x)
    {
        if (x <= 0.0) return 0.0;
        double xp = Math.Pow(x, 1.0 / PqP);
        double num = PqC1 - xp;
        double den = PqC3 * xp - PqC2;
        if (den >= 0.0) return 0.0;
        return 10000.0 * Math.Pow(num / den, 1.0 / PqN);
    }

    public static void RgbToJzazbz(double red, double green, double blue,
        out double jz, out double az, out double bz)
    {
        // sRGB → linear → XYZ (D65)
        RgbToXyz(red, green, blue, out double x, out double y, out double z);

        // Scale to absolute luminance (SDR content at 203 nits white)
        x *= SdrWhiteLuminance;
        y *= SdrWhiteLuminance;
        z *= SdrWhiteLuminance;

        // Modified XYZ
        double xp = JzB * x - (JzB - 1.0) * z;
        double yp = JzG * y - (JzG - 1.0) * x;

        // XYZ' → LMS (Ebner & Fairchild adapted matrix)
        double l = 0.41478972 * xp + 0.579999 * yp + 0.0146480 * z;
        double m = -0.20151000 * xp + 1.120649 * yp + 0.0531008 * z;
        double s = -0.01660080 * xp + 0.264800 * yp + 0.6684799 * z;

        // PQ transfer
        double lPq = PqForward(l);
        double mPq = PqForward(m);
        double sPq = PqForward(s);

        // PQ LMS → Izazbz
        double iz = 0.5 * lPq + 0.5 * mPq;
        az = 3.524000 * lPq - 4.066708 * mPq + 0.542708 * sPq;
        bz = 0.199076 * lPq + 1.096799 * mPq - 1.295875 * sPq;

        jz = (1.0 + JzD) * iz / (1.0 + JzD * iz) - JzD0;

        // Normalize: Jz is ~[0, 1], az/bz are ~[-0.5, 0.5] → shift to [0, 1]
        az = az * 0.5 + 0.5;
        bz = bz * 0.5 + 0.5;
    }

    public static void JzazbzToRgb(double jz, double az, double bz,
        out double red, out double green, out double blue)
    {
        // Denormalize
        az = (az - 0.5) * 2.0;
        bz = (bz - 0.5) * 2.0;

        double iz = (jz + JzD0) / (1.0 + JzD - JzD * (jz + JzD0));

        // Inverse Izazbz → PQ LMS
        double lPq = iz + 0.1386050433 * az + 0.0580473162 * bz;
        double mPq = iz - 0.1386050433 * az - 0.0580473162 * bz;
        double sPq = iz - 0.0960192421 * az - 0.8118918960 * bz;

        // Inverse PQ
        double l = PqInverse(lPq);
        double m = PqInverse(mPq);
        double s = PqInverse(sPq);

        // Inverse LMS → XYZ'
        double xp = 1.9242264358 * l - 1.0047923126 * m + 0.0376514040 * s;
        double yp = 0.3503167621 * l + 0.7264811939 * m - 0.0653844229 * s;
        double zRaw = -0.0909828110 * l - 0.3127282905 * m + 1.5227665613 * s;

        // Inverse modified XYZ
        double x = (xp + (JzB - 1.0) * zRaw) / JzB;
        double y = (yp + (JzG - 1.0) * x) / JzG;

        // Scale back from absolute luminance
        x /= SdrWhiteLuminance;
        y /= SdrWhiteLuminance;
        zRaw /= SdrWhiteLuminance;

        XyzToRgb(x, y, zRaw, out red, out green, out blue);
    }

    // --- JzCzhz (cylindrical JzAzBz) ---

    public static void RgbToJzczhz(double red, double green, double blue,
        out double jz, out double chroma, out double hue)
    {
        RgbToJzazbz(red, green, blue, out jz, out double az, out double bz);

        double aRaw = (az - 0.5) * 2.0;
        double bRaw = (bz - 0.5) * 2.0;

        chroma = Math.Sqrt(aRaw * aRaw + bRaw * bRaw);
        hue = Math.Atan2(bRaw, aRaw) * (180.0 / Math.PI) / 360.0;
        if (hue < 0.0)
            hue += 1.0;
    }

    public static void JzczhzToRgb(double jz, double chroma, double hue,
        out double red, out double green, out double blue)
    {
        double hRad = hue * 360.0 * (Math.PI / 180.0);
        double aRaw = chroma * Math.Cos(hRad);
        double bRaw = chroma * Math.Sin(hRad);

        double az = aRaw * 0.5 + 0.5;
        double bz = bRaw * 0.5 + 0.5;

        JzazbzToRgb(jz, az, bz, out red, out green, out blue);
    }

    // --- Display P3 ---
    // Wide-gamut display colorspace (Apple devices). Same D65 white point and sRGB TRC as sRGB,
    // but wider primaries covering ~25% more of CIE 1931.

    public static void RgbToDisplayP3(double red, double green, double blue,
        out double p3Red, out double p3Green, out double p3Blue)
    {
        // sRGB → linear sRGB → XYZ → linear P3 → P3 gamma (same as sRGB TRC)
        RgbToXyz(red, green, blue, out double x, out double y, out double z);

        // XYZ → linear Display P3
        double r = 2.4934969119 * x + (-0.9313836179) * y + (-0.4027107845) * z;
        double g = -0.8294889696 * x + 1.7626640603 * y + 0.0236246858 * z;
        double b = 0.0358458302 * x + (-0.0761723893) * y + 0.9568845240 * z;

        r = Math.Clamp(r, 0.0, 1.0);
        g = Math.Clamp(g, 0.0, 1.0);
        b = Math.Clamp(b, 0.0, 1.0);

        // Apply sRGB TRC (Display P3 uses the same transfer function as sRGB)
        p3Red = SrgbConverter.EncodeNormalized(r) * Quantum.MaxValue;
        p3Green = SrgbConverter.EncodeNormalized(g) * Quantum.MaxValue;
        p3Blue = SrgbConverter.EncodeNormalized(b) * Quantum.MaxValue;
    }

    public static void DisplayP3ToRgb(double p3Red, double p3Green, double p3Blue,
        out double red, out double green, out double blue)
    {
        // P3 gamma → linear P3 → XYZ → linear sRGB → sRGB gamma
        double r = SrgbConverter.DecodeNormalized(p3Red * Quantum.Scale);
        double g = SrgbConverter.DecodeNormalized(p3Green * Quantum.Scale);
        double b = SrgbConverter.DecodeNormalized(p3Blue * Quantum.Scale);

        // Linear Display P3 → XYZ
        double x = 0.4865709486 * r + 0.2656676932 * g + 0.1982172852 * b;
        double y = 0.2289745641 * r + 0.6917385218 * g + 0.0792869141 * b;
        double z = 0.0000000000 * r + 0.0451133819 * g + 1.0439443689 * b;

        XyzToRgb(x, y, z, out red, out green, out blue);
    }

    // --- ProPhoto RGB (ROMM RGB) ---
    // Wide-gamut photography colorspace with D50 white point and 1.8 gamma.
    // Covers ~100% of real-world surface colors. Requires D65↔D50 chromatic adaptation.

    private const double ProPhotoEt = 1.0 / 512.0;   // 0.001953125

    // D65 → D50 Bradford chromatic adaptation
    private static void XyzD65ToD50(double x65, double y65, double z65,
        out double x50, out double y50, out double z50)
    {
        x50 = 1.0479298208 * x65 + 0.0228856803 * y65 + (-0.0501704271) * z65;
        y50 = 0.0296278156 * x65 + 0.9904344267 * y65 + (-0.0170738250) * z65;
        z50 = -0.0092430581 * x65 + 0.0150551442 * y65 + 0.7518742814 * z65;
    }

    // D50 → D65 Bradford chromatic adaptation
    private static void XyzD50ToD65(double x50, double y50, double z50,
        out double x65, out double y65, out double z65)
    {
        x65 = 0.9555766 * x50 + (-0.0230393) * y50 + 0.0631636 * z50;
        y65 = -0.0282895 * x50 + 1.0099416 * y50 + 0.0210077 * z50;
        z65 = 0.0122982 * x50 + (-0.0204830) * y50 + 1.3299098 * z50;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ProPhotoEncode(double linear)
    {
        if (linear < 0.0) return 0.0;
        return linear >= ProPhotoEt
            ? Math.Pow(linear, 1.0 / 1.8)
            : 16.0 * linear;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ProPhotoDecode(double encoded)
    {
        if (encoded < 0.0) return 0.0;
        return encoded >= 16.0 * ProPhotoEt
            ? Math.Pow(encoded, 1.8)
            : encoded / 16.0;
    }

    public static void RgbToProPhoto(double red, double green, double blue,
        out double ppRed, out double ppGreen, out double ppBlue)
    {
        // sRGB → linear sRGB → XYZ (D65) → XYZ (D50) → linear ProPhoto → ProPhoto gamma
        RgbToXyz(red, green, blue, out double x65, out double y65, out double z65);
        XyzD65ToD50(x65, y65, z65, out double x50, out double y50, out double z50);

        // XYZ (D50) → linear ProPhoto RGB
        double r = 1.3459433009 * x50 + (-0.2556075108) * y50 + (-0.0511118843) * z50;
        double g = -0.5445989113 * x50 + 1.5081673660 * y50 + 0.0205351443 * z50;
        double b = 0.0000000000 * x50 + 0.0000000000 * y50 + 1.2118127757 * z50;

        r = Math.Clamp(r, 0.0, 1.0);
        g = Math.Clamp(g, 0.0, 1.0);
        b = Math.Clamp(b, 0.0, 1.0);

        ppRed = ProPhotoEncode(r) * Quantum.MaxValue;
        ppGreen = ProPhotoEncode(g) * Quantum.MaxValue;
        ppBlue = ProPhotoEncode(b) * Quantum.MaxValue;
    }

    public static void ProPhotoToRgb(double ppRed, double ppGreen, double ppBlue,
        out double red, out double green, out double blue)
    {
        // ProPhoto gamma → linear ProPhoto → XYZ (D50) → XYZ (D65) → linear sRGB → sRGB
        double r = ProPhotoDecode(ppRed * Quantum.Scale);
        double g = ProPhotoDecode(ppGreen * Quantum.Scale);
        double b = ProPhotoDecode(ppBlue * Quantum.Scale);

        // Linear ProPhoto → XYZ (D50)
        double x50 = 0.7977604896 * r + 0.1351917082 * g + 0.0313493495 * b;
        double y50 = 0.2880711282 * r + 0.7118432178 * g + 0.0000856540 * b;
        double z50 = 0.0000000000 * r + 0.0000000000 * g + 0.8251046026 * b;

        XyzD50ToD65(x50, y50, z50, out double x65, out double y65, out double z65);
        XyzToRgb(x65, y65, z65, out red, out green, out blue);
    }
}
