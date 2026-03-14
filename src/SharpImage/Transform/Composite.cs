using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Transform;

/// <summary>
/// Image compositing operations — overlaying one image onto another using various blend modes (Over, Multiply, Screen,
/// etc.).
/// </summary>
public static class Composite
{
    /// <summary>
    /// Composites the overlay image onto the base image at the specified position. Modifies the base image in-place.
    /// </summary>
    public static void Apply(ImageFrame baseImage, ImageFrame overlay,
        int offsetX, int offsetY, CompositeMode mode = CompositeMode.Over)
    {
        int baseWidth = (int)baseImage.Columns;
        int baseHeight = (int)baseImage.Rows;
        int overlayWidth = (int)overlay.Columns;
        int overlayHeight = (int)overlay.Rows;

        int startX = Math.Max(0, offsetX);
        int startY = Math.Max(0, offsetY);
        int endX = Math.Min(baseWidth, offsetX + overlayWidth);
        int endY = Math.Min(baseHeight, offsetY + overlayHeight);

        if (startX >= endX || startY >= endY)
        {
            return;
        }

        int baseChannels = baseImage.NumberOfChannels;
        int overlayChannels = overlay.NumberOfChannels;

        Parallel.For(startY, endY, y =>
        {
            var baseRow = baseImage.GetPixelRowForWrite(y);
            var overlayRow = overlay.GetPixelRow(y - offsetY);

            for (int x = startX;x < endX;x++)
            {
                int baseOff = x * baseChannels;
                int overlayOff = (x - offsetX) * overlayChannels;

                // Get overlay alpha (1.0 if no alpha channel)
                float overlayAlpha = overlayChannels > 3
                    ? overlayRow[overlayOff + 3] * (float)Quantum.Scale
                    : 1.0f;

                float baseAlpha = baseChannels > 3
                    ? baseRow[baseOff + 3] * (float)Quantum.Scale
                    : 1.0f;

                // Get normalized base and overlay colors
                float bR = baseRow[baseOff] * (float)Quantum.Scale;
                float bG = baseRow[baseOff + 1] * (float)Quantum.Scale;
                float bB = baseRow[baseOff + 2] * (float)Quantum.Scale;

                float oR = overlayRow[overlayOff] * (float)Quantum.Scale;
                float oG = overlayRow[overlayOff + 1] * (float)Quantum.Scale;
                float oB = overlayRow[overlayOff + 2] * (float)Quantum.Scale;

                float rR, rG, rB, rA;

                switch (mode)
                {
                    case CompositeMode.Over:
                        rA = overlayAlpha + baseAlpha * (1 - overlayAlpha);
                        if (rA > 0)
                        {
                            rR = (oR * overlayAlpha + bR * baseAlpha * (1 - overlayAlpha)) / rA;
                            rG = (oG * overlayAlpha + bG * baseAlpha * (1 - overlayAlpha)) / rA;
                            rB = (oB * overlayAlpha + bB * baseAlpha * (1 - overlayAlpha)) / rA;
                        }
                        else
                        {
                            rR = rG = rB = 0;
                        }
                        break;

                    case CompositeMode.Multiply:
                        rR = BlendWithAlpha(bR, oR * bR, overlayAlpha);
                        rG = BlendWithAlpha(bG, oG * bG, overlayAlpha);
                        rB = BlendWithAlpha(bB, oB * bB, overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.Screen:
                        rR = BlendWithAlpha(bR, 1 - (1 - oR) * (1 - bR), overlayAlpha);
                        rG = BlendWithAlpha(bG, 1 - (1 - oG) * (1 - bG), overlayAlpha);
                        rB = BlendWithAlpha(bB, 1 - (1 - oB) * (1 - bB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.Add:
                        rR = BlendWithAlpha(bR, Math.Min(1.0f, bR + oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, Math.Min(1.0f, bG + oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, Math.Min(1.0f, bB + oB), overlayAlpha);
                        rA = Math.Min(1.0f, baseAlpha + overlayAlpha);
                        break;

                    case CompositeMode.Subtract:
                        rR = BlendWithAlpha(bR, Math.Max(0, bR - oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, Math.Max(0, bG - oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, Math.Max(0, bB - oB), overlayAlpha);
                        rA = baseAlpha;
                        break;

                    case CompositeMode.Overlay:
                        rR = BlendWithAlpha(bR, OverlayBlend(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, OverlayBlend(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, OverlayBlend(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.Replace:
                        rR = oR;
                        rG = oG;
                        rB = oB;
                        rA = overlayAlpha;
                        break;

                    case CompositeMode.Darken:
                        rR = BlendWithAlpha(bR, Math.Min(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, Math.Min(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, Math.Min(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.Lighten:
                        rR = BlendWithAlpha(bR, Math.Max(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, Math.Max(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, Math.Max(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.ColorDodge:
                        rR = BlendWithAlpha(bR, ColorDodgeBlend(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, ColorDodgeBlend(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, ColorDodgeBlend(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.ColorBurn:
                        rR = BlendWithAlpha(bR, ColorBurnBlend(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, ColorBurnBlend(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, ColorBurnBlend(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.HardLight:
                        rR = BlendWithAlpha(bR, HardLightBlend(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, HardLightBlend(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, HardLightBlend(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.SoftLight:
                        rR = BlendWithAlpha(bR, SoftLightBlend(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, SoftLightBlend(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, SoftLightBlend(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.Difference:
                        rR = BlendWithAlpha(bR, Math.Abs(bR - oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, Math.Abs(bG - oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, Math.Abs(bB - oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.Exclusion:
                        rR = BlendWithAlpha(bR, bR + oR - 2 * bR * oR, overlayAlpha);
                        rG = BlendWithAlpha(bG, bG + oG - 2 * bG * oG, overlayAlpha);
                        rB = BlendWithAlpha(bB, bB + oB - 2 * bB * oB, overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.Dissolve:
                        // Probabilistic dissolve — use hash-based pseudo-random
                        int hash = HashCode.Combine(x, y);
                        float threshold = (hash & 0xFFFF) / 65535.0f;
                        if (threshold < overlayAlpha)
                        {
                            rR = oR; rG = oG; rB = oB; rA = 1.0f;
                        }
                        else
                        {
                            rR = bR; rG = bG; rB = bB; rA = baseAlpha;
                        }
                        break;

                    case CompositeMode.Atop:
                        // Porter-Duff Atop: overlay appears only where base has alpha
                        rR = oR * overlayAlpha * baseAlpha + bR * baseAlpha * (1 - overlayAlpha);
                        rG = oG * overlayAlpha * baseAlpha + bG * baseAlpha * (1 - overlayAlpha);
                        rB = oB * overlayAlpha * baseAlpha + bB * baseAlpha * (1 - overlayAlpha);
                        rA = baseAlpha;
                        if (rA > 0) { rR /= rA; rG /= rA; rB /= rA; }
                        break;

                    case CompositeMode.In:
                        // Porter-Duff In: overlay clipped by base alpha
                        rR = oR;
                        rG = oG;
                        rB = oB;
                        rA = overlayAlpha * baseAlpha;
                        break;

                    case CompositeMode.Out:
                        // Porter-Duff Out: overlay where base is transparent
                        rR = oR;
                        rG = oG;
                        rB = oB;
                        rA = overlayAlpha * (1 - baseAlpha);
                        break;

                    case CompositeMode.Xor:
                        // Porter-Duff Xor: non-overlapping regions only
                        rA = overlayAlpha * (1 - baseAlpha) + baseAlpha * (1 - overlayAlpha);
                        if (rA > 0)
                        {
                            rR = (oR * overlayAlpha * (1 - baseAlpha) + bR * baseAlpha * (1 - overlayAlpha)) / rA;
                            rG = (oG * overlayAlpha * (1 - baseAlpha) + bG * baseAlpha * (1 - overlayAlpha)) / rA;
                            rB = (oB * overlayAlpha * (1 - baseAlpha) + bB * baseAlpha * (1 - overlayAlpha)) / rA;
                        }
                        else
                        {
                            rR = rG = rB = 0;
                        }
                        break;

                    case CompositeMode.Plus:
                        rR = Math.Min(1.0f, bR * baseAlpha + oR * overlayAlpha);
                        rG = Math.Min(1.0f, bG * baseAlpha + oG * overlayAlpha);
                        rB = Math.Min(1.0f, bB * baseAlpha + oB * overlayAlpha);
                        rA = Math.Min(1.0f, baseAlpha + overlayAlpha);
                        break;

                    case CompositeMode.Minus:
                        rR = Math.Max(0, bR * baseAlpha - oR * overlayAlpha);
                        rG = Math.Max(0, bG * baseAlpha - oG * overlayAlpha);
                        rB = Math.Max(0, bB * baseAlpha - oB * overlayAlpha);
                        rA = baseAlpha;
                        break;

                    case CompositeMode.ModulusAdd:
                        rR = (bR + oR) % 1.0f;
                        rG = (bG + oG) % 1.0f;
                        rB = (bB + oB) % 1.0f;
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.ModulusSubtract:
                        rR = (bR - oR + 1.0f) % 1.0f;
                        rG = (bG - oG + 1.0f) % 1.0f;
                        rB = (bB - oB + 1.0f) % 1.0f;
                        rA = baseAlpha;
                        break;

                    case CompositeMode.Bumpmap:
                        float lum = 0.299f * oR + 0.587f * oG + 0.114f * oB;
                        rR = BlendWithAlpha(bR, bR * lum, overlayAlpha);
                        rG = BlendWithAlpha(bG, bG * lum, overlayAlpha);
                        rB = BlendWithAlpha(bB, bB * lum, overlayAlpha);
                        rA = baseAlpha;
                        break;

                    case CompositeMode.HardMix:
                        rR = BlendWithAlpha(bR, HardMixBlend(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, HardMixBlend(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, HardMixBlend(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.LinearLight:
                        rR = BlendWithAlpha(bR, LinearLightBlend(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, LinearLightBlend(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, LinearLightBlend(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.VividLight:
                        rR = BlendWithAlpha(bR, VividLightBlend(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, VividLightBlend(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, VividLightBlend(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    case CompositeMode.PinLight:
                        rR = BlendWithAlpha(bR, PinLightBlend(bR, oR), overlayAlpha);
                        rG = BlendWithAlpha(bG, PinLightBlend(bG, oG), overlayAlpha);
                        rB = BlendWithAlpha(bB, PinLightBlend(bB, oB), overlayAlpha);
                        rA = Math.Max(baseAlpha, overlayAlpha);
                        break;

                    default:
                        goto case CompositeMode.Over;
                }

                baseRow[baseOff] = (ushort)Math.Clamp(rR * Quantum.MaxValue + 0.5f, 0, Quantum.MaxValue);
                baseRow[baseOff + 1] = (ushort)Math.Clamp(rG * Quantum.MaxValue + 0.5f, 0, Quantum.MaxValue);
                baseRow[baseOff + 2] = (ushort)Math.Clamp(rB * Quantum.MaxValue + 0.5f, 0, Quantum.MaxValue);

                if (baseChannels > 3)
                {
                    baseRow[baseOff + 3] = (ushort)Math.Clamp(rA * Quantum.MaxValue + 0.5f, 0, Quantum.MaxValue);
                }
            }
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float BlendWithAlpha(float baseVal, float blendedVal, float overlayAlpha)
        => baseVal * (1 - overlayAlpha) + blendedVal * overlayAlpha;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float OverlayBlend(float baseVal, float overlayVal)
        => baseVal < 0.5f
            ? 2 * baseVal * overlayVal
            : 1 - 2 * (1 - baseVal) * (1 - overlayVal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HardLightBlend(float baseVal, float overlayVal)
        => overlayVal < 0.5f
            ? 2 * baseVal * overlayVal
            : 1 - 2 * (1 - baseVal) * (1 - overlayVal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftLightBlend(float b, float o)
    {
        // W3C compositing spec formula
        if (o <= 0.5f)
            return b - (1 - 2 * o) * b * (1 - b);
        float d = b <= 0.25f ? ((16 * b - 12) * b + 4) * b : MathF.Sqrt(b);
        return b + (2 * o - 1) * (d - b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ColorDodgeBlend(float b, float o)
    {
        if (b <= 0) return 0;
        if (o >= 1) return 1;
        return Math.Min(1.0f, b / (1 - o));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ColorBurnBlend(float b, float o)
    {
        if (b >= 1) return 1;
        if (o <= 0) return 0;
        return 1 - Math.Min(1.0f, (1 - b) / o);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LinearLightBlend(float b, float o)
        => Math.Clamp(b + 2 * o - 1, 0, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float VividLightBlend(float b, float o)
        => o <= 0.5f ? ColorBurnBlend(b, 2 * o) : ColorDodgeBlend(b, 2 * o - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PinLightBlend(float b, float o)
        => o < 0.5f ? Math.Min(b, 2 * o) : Math.Max(b, 2 * o - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HardMixBlend(float b, float o)
        => b + o >= 1.0f ? 1.0f : 0.0f;
}

/// <summary>
/// Composite blend modes for overlaying images.
/// </summary>
public enum CompositeMode
{
    /// <summary>Porter-Duff Over — standard alpha compositing.</summary>
    Over,
    /// <summary>Multiply blend — darkens by multiplying channel values.</summary>
    Multiply,
    /// <summary>Screen blend — lightens using inverse multiply.</summary>
    Screen,
    /// <summary>Overlay blend — combines Multiply and Screen for contrast.</summary>
    Overlay,
    /// <summary>Additive blend — clipped sum of channels.</summary>
    Add,
    /// <summary>Subtractive blend — clipped difference of channels.</summary>
    Subtract,
    /// <summary>Complete replacement, no blending.</summary>
    Replace,
    /// <summary>Per-channel minimum (keeps darkest).</summary>
    Darken,
    /// <summary>Per-channel maximum (keeps lightest).</summary>
    Lighten,
    /// <summary>Color Dodge — brightens base by dividing by inverse overlay.</summary>
    ColorDodge,
    /// <summary>Color Burn — darkens base by dividing inverted base by overlay.</summary>
    ColorBurn,
    /// <summary>Hard Light — Overlay with layers swapped.</summary>
    HardLight,
    /// <summary>Soft Light — gentle contrast using W3C formula.</summary>
    SoftLight,
    /// <summary>Absolute difference between channels.</summary>
    Difference,
    /// <summary>Similar to Difference but lower contrast.</summary>
    Exclusion,
    /// <summary>Probabilistic compositing — pixels randomly chosen by alpha.</summary>
    Dissolve,
    /// <summary>Porter-Duff Atop — overlay only where base is opaque.</summary>
    Atop,
    /// <summary>Porter-Duff In — overlay clipped to base alpha.</summary>
    In,
    /// <summary>Porter-Duff Out — overlay where base is transparent.</summary>
    Out,
    /// <summary>Porter-Duff Xor — non-overlapping regions only.</summary>
    Xor,
    /// <summary>Linear addition of premultiplied values.</summary>
    Plus,
    /// <summary>Linear subtraction of premultiplied values.</summary>
    Minus,
    /// <summary>Modular addition (wrapping).</summary>
    ModulusAdd,
    /// <summary>Modular subtraction (wrapping).</summary>
    ModulusSubtract,
    /// <summary>Bumpmapping — modulates base by overlay luminance.</summary>
    Bumpmap,
    /// <summary>Hard Mix — posterizes to black or white based on sum.</summary>
    HardMix,
    /// <summary>Linear Light — combines Linear Burn and Linear Dodge.</summary>
    LinearLight,
    /// <summary>Vivid Light — combines Color Burn and Color Dodge.</summary>
    VividLight,
    /// <summary>Pin Light — selective replacement based on overlay value.</summary>
    PinLight
}
