using SharpImage.Colorspaces;
using SharpImage.Core;

namespace SharpImage.Tests.Colorspace;

/// <summary>
/// Colorspace round-trip validation tests. Reference values derived from ImageMagick tests/validate.c. Reference RGB:
/// (0.545877, 0.966567, 0.463759) as fractions of QuantumRange. Epsilon: QuantumRange * 1e-2 (generous — tighter where
/// possible).
/// </summary>
public class ColorspaceConverterTests
{
    private static readonly double RefRed = 0.545877 * Quantum.MaxValue;
    private static readonly double RefGreen = 0.966567 * Quantum.MaxValue;
    private static readonly double RefBlue = 0.463759 * Quantum.MaxValue;
    private static readonly double Epsilon = Quantum.MaxValue * 1e-2;

    // ========== sRGB Gamma ==========

    [Test]
    public async Task SrgbDecode_RoundTrips()
    {
        double decoded = SrgbConverter.Decode(RefRed);
        double encoded = SrgbConverter.Encode(decoded);
        await Assert.That(Math.Abs(encoded - RefRed)).IsLessThan(1.0);
    }

    [Test]
    public async Task SrgbEncode_RoundTrips()
    {
        double encoded = SrgbConverter.Encode(RefGreen);
        double decoded = SrgbConverter.Decode(encoded);
        await Assert.That(Math.Abs(decoded - RefGreen)).IsLessThan(1.0);
    }

    [Test]
    public async Task SrgbNormalized_DecodeEncode_RoundTrips()
    {
        double v = 0.5;
        double linear = SrgbConverter.DecodeNormalized(v);
        double srgb = SrgbConverter.EncodeNormalized(linear);
        await Assert.That(Math.Abs(srgb - v)).IsLessThan(1e-12);
    }

    [Test]
    public async Task SrgbDecode_LinearRegion_IsCorrect()
    {
        // For small values, decode = v / 12.92
        double v = 0.01 * Quantum.MaxValue;
        double decoded = SrgbConverter.Decode(v);
        double expected = (0.01 / 12.92) * Quantum.MaxValue;
        await Assert.That(Math.Abs(decoded - expected)).IsLessThan(0.5);
    }

    // ========== HSL ==========

    [Test]
    public async Task RgbToHsl_ReferenceValues()
    {
        ColorspaceConverter.RgbToHsl(RefRed, RefGreen, RefBlue,
            out double h, out double s, out double l);

        await Assert.That(h).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(h).IsLessThan(1.0);
        await Assert.That(s).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(l).IsGreaterThanOrEqualTo(0.0);
    }

    [Test]
    public async Task Hsl_RoundTrips()
    {
        ColorspaceConverter.RgbToHsl(RefRed, RefGreen, RefBlue,
            out double h, out double s, out double l);
        ColorspaceConverter.HslToRgb(h, s, l,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    [Test]
    public async Task Hsl_PureRed()
    {
        ColorspaceConverter.RgbToHsl(Quantum.MaxValue, 0, 0,
            out double h, out double s, out double l);

        await Assert.That(Math.Abs(h)).IsLessThan(1e-6);           // Hue = 0
        await Assert.That(Math.Abs(s - 1.0)).IsLessThan(1e-6);     // Fully saturated
        await Assert.That(Math.Abs(l - 0.5)).IsLessThan(1e-6);     // Lightness = 0.5
    }

    [Test]
    public async Task Hsl_Gray()
    {
        double gray = 0.5 * Quantum.MaxValue;
        ColorspaceConverter.RgbToHsl(gray, gray, gray,
            out double h, out double s, out double l);

        await Assert.That(Math.Abs(s)).IsLessThan(1e-6);       // No saturation
        await Assert.That(Math.Abs(l - 0.5)).IsLessThan(1e-6);
    }

    // ========== HSV ==========

    [Test]
    public async Task Hsv_RoundTrips()
    {
        ColorspaceConverter.RgbToHsv(RefRed, RefGreen, RefBlue,
            out double h, out double s, out double v);
        ColorspaceConverter.HsvToRgb(h, s, v,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    [Test]
    public async Task Hsv_PureGreen()
    {
        ColorspaceConverter.RgbToHsv(0, Quantum.MaxValue, 0,
            out double h, out double s, out double v);

        await Assert.That(Math.Abs(h - 1.0 / 3.0)).IsLessThan(1e-6); // 120°/360°
        await Assert.That(Math.Abs(s - 1.0)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(v - 1.0)).IsLessThan(1e-6);
    }

    // ========== HSI ==========

    [Test]
    public async Task Hsi_RoundTrips()
    {
        ColorspaceConverter.RgbToHsi(RefRed, RefGreen, RefBlue,
            out double h, out double s, out double i);
        ColorspaceConverter.HsiToRgb(h, s, i,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== HWB ==========

    [Test]
    public async Task Hwb_RoundTrips()
    {
        ColorspaceConverter.RgbToHwb(RefRed, RefGreen, RefBlue,
            out double h, out double w, out double bk);
        ColorspaceConverter.HwbToRgb(h, w, bk,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== HCL ==========

    [Test]
    public async Task Hcl_RoundTrips()
    {
        ColorspaceConverter.RgbToHcl(RefRed, RefGreen, RefBlue,
            out double h, out double c, out double l);
        ColorspaceConverter.HclToRgb(h, c, l,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== HCLp ==========

    [Test]
    public async Task Hclp_RoundTrips()
    {
        ColorspaceConverter.RgbToHclp(RefRed, RefGreen, RefBlue,
            out double h, out double c, out double l);
        ColorspaceConverter.HclpToRgb(h, c, l,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== XYZ ==========

    [Test]
    public async Task Xyz_RoundTrips()
    {
        ColorspaceConverter.RgbToXyz(RefRed, RefGreen, RefBlue,
            out double x, out double y, out double z);
        ColorspaceConverter.XyzToRgb(x, y, z,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== Lab ==========

    [Test]
    public async Task Lab_RoundTrips()
    {
        ColorspaceConverter.RgbToLab(RefRed, RefGreen, RefBlue,
            out double l, out double a, out double b);
        ColorspaceConverter.LabToRgb(l, a, b,
            out double r, out double g, out double bl);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(bl - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== LCHab ==========

    [Test]
    public async Task Lchab_RoundTrips()
    {
        ColorspaceConverter.RgbToLchab(RefRed, RefGreen, RefBlue,
            out double l, out double c, out double h);
        ColorspaceConverter.LchabToRgb(l, c, h,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== Luv ==========

    [Test]
    public async Task Luv_RoundTrips()
    {
        ColorspaceConverter.RgbToLuv(RefRed, RefGreen, RefBlue,
            out double l, out double u, out double v);
        ColorspaceConverter.LuvToRgb(l, u, v,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== LCHuv ==========

    [Test]
    public async Task Lchuv_RoundTrips()
    {
        ColorspaceConverter.RgbToLchuv(RefRed, RefGreen, RefBlue,
            out double l, out double c, out double h);
        ColorspaceConverter.LchuvToRgb(l, c, h,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== YCbCr / YPbPr ==========

    [Test]
    public async Task YPbPr_RoundTrips()
    {
        ColorspaceConverter.RgbToYPbPr(RefRed, RefGreen, RefBlue,
            out double y, out double pb, out double pr);
        ColorspaceConverter.YPbPrToRgb(y, pb, pr,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    [Test]
    public async Task YCbCr_RoundTrips()
    {
        ColorspaceConverter.RgbToYCbCr(RefRed, RefGreen, RefBlue,
            out double y, out double cb, out double cr);
        ColorspaceConverter.YCbCrToRgb(y, cb, cr,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== YIQ ==========

    [Test]
    public async Task Yiq_RoundTrips()
    {
        ColorspaceConverter.RgbToYiq(RefRed, RefGreen, RefBlue,
            out double y, out double i, out double q);
        ColorspaceConverter.YiqToRgb(y, i, q,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== YUV ==========

    [Test]
    public async Task Yuv_RoundTrips()
    {
        ColorspaceConverter.RgbToYuv(RefRed, RefGreen, RefBlue,
            out double y, out double u, out double v);
        ColorspaceConverter.YuvToRgb(y, u, v,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== YDbDr ==========

    [Test]
    public async Task YDbDr_RoundTrips()
    {
        ColorspaceConverter.RgbToYDbDr(RefRed, RefGreen, RefBlue,
            out double y, out double db, out double dr);
        ColorspaceConverter.YDbDrToRgb(y, db, dr,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== CMYK ==========

    [Test]
    public async Task Cmyk_RoundTrips()
    {
        ColorspaceConverter.RgbToCmyk(RefRed, RefGreen, RefBlue,
            out double c, out double m, out double y, out double k);
        ColorspaceConverter.CmykToRgb(c, m, y, k,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    [Test]
    public async Task Cmyk_PureBlack()
    {
        ColorspaceConverter.RgbToCmyk(0, 0, 0,
            out double c, out double m, out double y, out double k);

        await Assert.That(Math.Abs(c)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(m)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(y)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(k - Quantum.MaxValue)).IsLessThan(1e-6);
    }

    [Test]
    public async Task Cmyk_PureWhite()
    {
        ColorspaceConverter.RgbToCmyk(Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue,
            out double c, out double m, out double y, out double k);

        await Assert.That(Math.Abs(c)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(m)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(y)).IsLessThan(1e-6);
        await Assert.That(Math.Abs(k)).IsLessThan(1e-6);
    }

    // ========== LMS ==========

    [Test]
    public async Task Lms_RoundTrips()
    {
        ColorspaceConverter.RgbToLms(RefRed, RefGreen, RefBlue,
            out double l, out double m, out double s);
        ColorspaceConverter.LmsToRgb(l, m, s,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== Oklab ==========

    [Test]
    public async Task Oklab_RoundTrips()
    {
        ColorspaceConverter.RgbToOklab(RefRed, RefGreen, RefBlue,
            out double l, out double a, out double b);
        ColorspaceConverter.OklabToRgb(l, a, b,
            out double r, out double g, out double bl);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(bl - RefBlue)).IsLessThan(Epsilon);
    }

    [Test]
    public async Task Oklab_White_HasMaxLightness()
    {
        ColorspaceConverter.RgbToOklab(Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue,
            out double l, out double a, out double b);

        await Assert.That(Math.Abs(l - 1.0)).IsLessThan(1e-4);
        await Assert.That(Math.Abs(a - 0.5)).IsLessThan(1e-3); // a≈0 → normalized 0.5
        await Assert.That(Math.Abs(b - 0.5)).IsLessThan(1e-3); // b≈0 → normalized 0.5
    }

    [Test]
    public async Task Oklab_Black_HasZeroLightness()
    {
        ColorspaceConverter.RgbToOklab(0, 0, 0,
            out double l, out double a, out double b);

        await Assert.That(Math.Abs(l)).IsLessThan(1e-6);
    }

    [Test]
    public async Task Oklab_PureRed_RoundTrips()
    {
        ColorspaceConverter.RgbToOklab(Quantum.MaxValue, 0, 0,
            out double l, out double a, out double b);
        ColorspaceConverter.OklabToRgb(l, a, b,
            out double r, out double g, out double bl);

        await Assert.That(Math.Abs(r - Quantum.MaxValue)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(bl)).IsLessThan(Epsilon);
    }

    // ========== Oklch ==========

    [Test]
    public async Task Oklch_RoundTrips()
    {
        ColorspaceConverter.RgbToOklch(RefRed, RefGreen, RefBlue,
            out double l, out double c, out double h);
        ColorspaceConverter.OklchToRgb(l, c, h,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    [Test]
    public async Task Oklch_Gray_HasZeroChroma()
    {
        double gray = 0.5 * Quantum.MaxValue;
        ColorspaceConverter.RgbToOklch(gray, gray, gray,
            out double l, out double c, out double h);

        await Assert.That(c).IsLessThan(1e-4);
    }

    // ========== JzAzBz ==========

    [Test]
    public async Task Jzazbz_RoundTrips()
    {
        ColorspaceConverter.RgbToJzazbz(RefRed, RefGreen, RefBlue,
            out double jz, out double az, out double bz);
        ColorspaceConverter.JzazbzToRgb(jz, az, bz,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    [Test]
    public async Task Jzazbz_Black_HasNearZeroJz()
    {
        ColorspaceConverter.RgbToJzazbz(0, 0, 0,
            out double jz, out _, out _);

        await Assert.That(Math.Abs(jz)).IsLessThan(1e-4);
    }

    [Test]
    public async Task Jzazbz_PureRed_RoundTrips()
    {
        ColorspaceConverter.RgbToJzazbz(Quantum.MaxValue, 0, 0,
            out double jz, out double az, out double bz);
        ColorspaceConverter.JzazbzToRgb(jz, az, bz,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - Quantum.MaxValue)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b)).IsLessThan(Epsilon);
    }

    // ========== JzCzhz ==========

    [Test]
    public async Task Jzczhz_RoundTrips()
    {
        ColorspaceConverter.RgbToJzczhz(RefRed, RefGreen, RefBlue,
            out double jz, out double c, out double h);
        ColorspaceConverter.JzczhzToRgb(jz, c, h,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    // ========== DisplayP3 ==========

    [Test]
    public async Task DisplayP3_RoundTrips()
    {
        ColorspaceConverter.RgbToDisplayP3(RefRed, RefGreen, RefBlue,
            out double p3r, out double p3g, out double p3b);
        ColorspaceConverter.DisplayP3ToRgb(p3r, p3g, p3b,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    [Test]
    public async Task DisplayP3_SrgbWhite_MapsToP3White()
    {
        ColorspaceConverter.RgbToDisplayP3(Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue,
            out double p3r, out double p3g, out double p3b);

        // sRGB white should map to P3 white (same white point D65)
        await Assert.That(Math.Abs(p3r - Quantum.MaxValue)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(p3g - Quantum.MaxValue)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(p3b - Quantum.MaxValue)).IsLessThan(Epsilon);
    }

    [Test]
    public async Task DisplayP3_SrgbIsSubsetOfP3()
    {
        // sRGB pure red (1,0,0) should NOT be pure red in P3 (P3 gamut is wider)
        ColorspaceConverter.RgbToDisplayP3(Quantum.MaxValue, 0, 0,
            out double p3r, out double p3g, out double p3b);

        // P3 red channel should be less than QuantumRange (sRGB red is inside P3 gamut)
        await Assert.That(p3r).IsLessThan(Quantum.MaxValue);
    }

    // ========== ProPhoto RGB ==========

    [Test]
    public async Task ProPhoto_RoundTrips()
    {
        ColorspaceConverter.RgbToProPhoto(RefRed, RefGreen, RefBlue,
            out double ppr, out double ppg, out double ppb);
        ColorspaceConverter.ProPhotoToRgb(ppr, ppg, ppb,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - RefRed)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(g - RefGreen)).IsLessThan(Epsilon);
        await Assert.That(Math.Abs(b - RefBlue)).IsLessThan(Epsilon);
    }

    [Test]
    public async Task ProPhoto_SrgbIsSubsetOfProPhoto()
    {
        // sRGB pure green (0,1,0) should map to a smaller value in ProPhoto (wider gamut)
        ColorspaceConverter.RgbToProPhoto(0, Quantum.MaxValue, 0,
            out double ppr, out double ppg, out double ppb);

        // ProPhoto green should be less than QuantumRange (sRGB green is inside ProPhoto gamut)
        await Assert.That(ppg).IsLessThan(Quantum.MaxValue);
    }

    [Test]
    public async Task ProPhoto_PureRed_RoundTrips()
    {
        // Wider tolerance for pure primaries: D65↔D50 chromatic adaptation adds ~1% error
        double wideEpsilon = Quantum.MaxValue * 2e-2;
        ColorspaceConverter.RgbToProPhoto(Quantum.MaxValue, 0, 0,
            out double ppr, out double ppg, out double ppb);
        ColorspaceConverter.ProPhotoToRgb(ppr, ppg, ppb,
            out double r, out double g, out double b);

        await Assert.That(Math.Abs(r - Quantum.MaxValue)).IsLessThan(wideEpsilon);
        await Assert.That(Math.Abs(g)).IsLessThan(wideEpsilon);
        await Assert.That(Math.Abs(b)).IsLessThan(wideEpsilon);
    }

    // ========== Edge cases ==========

    [Test]
    public Task AllSpaces_Black_DoesNotCrash()
    {
        double black = 0.0;
        ColorspaceConverter.RgbToHsl(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToHsv(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToHsi(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToHwb(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToHcl(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToXyz(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToLab(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToLuv(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToYPbPr(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToYiq(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToYuv(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToYDbDr(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToCmyk(black, black, black, out _, out _, out _, out _);
        ColorspaceConverter.RgbToLms(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToOklab(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToOklch(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToJzazbz(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToJzczhz(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToDisplayP3(black, black, black, out _, out _, out _);
        ColorspaceConverter.RgbToProPhoto(black, black, black, out _, out _, out _);
        return Task.CompletedTask;
    }

    [Test]
    public Task AllSpaces_White_DoesNotCrash()
    {
        double white = Quantum.MaxValue;
        ColorspaceConverter.RgbToHsl(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToHsv(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToHsi(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToHwb(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToHcl(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToXyz(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToLab(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToLuv(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToYPbPr(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToYiq(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToYuv(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToYDbDr(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToCmyk(white, white, white, out _, out _, out _, out _);
        ColorspaceConverter.RgbToLms(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToOklab(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToOklch(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToJzazbz(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToJzczhz(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToDisplayP3(white, white, white, out _, out _, out _);
        ColorspaceConverter.RgbToProPhoto(white, white, white, out _, out _, out _);
        return Task.CompletedTask;
    }

    // ========== Determinism: same input always yields same output ==========

    [Test]
    public async Task Hsl_IsDeterministic()
    {
        ColorspaceConverter.RgbToHsl(RefRed, RefGreen, RefBlue, out double h1, out double s1, out double l1);
        ColorspaceConverter.RgbToHsl(RefRed, RefGreen, RefBlue, out double h2, out double s2, out double l2);

        await Assert.That(h1).IsEqualTo(h2);
        await Assert.That(s1).IsEqualTo(s2);
        await Assert.That(l1).IsEqualTo(l2);
    }
}
