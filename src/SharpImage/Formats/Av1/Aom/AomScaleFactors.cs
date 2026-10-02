using System;

namespace SharpImage.Formats.Av1;

/// <summary>struct scale_factors (av1/common/scale.{c,h}).</summary>
internal sealed class AomScaleFactors
{
    public const int REF_SCALE_SHIFT = 14, REF_NO_SCALE = 1 << REF_SCALE_SHIFT, REF_INVALID_SCALE = -1;
    private const int SUBPEL_BITS = 4, SCALE_SUBPEL_BITS = 10, SCALE_EXTRA_BITS = SCALE_SUBPEL_BITS - SUBPEL_BITS;
    public int XScaleFp, YScaleFp, XStepQ4, YStepQ4;

    /// <summary>av1_setup_scale_factors_for_frame.</summary>
    public static AomScaleFactors ForFrame(int otherW, int otherH, int thisW, int thisH)
    {
        var sf = new AomScaleFactors();
        if (!(2 * thisW >= otherW && 2 * thisH >= otherH && thisW <= 16 * otherW && thisH <= 16 * otherH))
        {
            sf.XScaleFp = REF_INVALID_SCALE;
            sf.YScaleFp = REF_INVALID_SCALE;
            return sf;
        }
        sf.XScaleFp = ((otherW << REF_SCALE_SHIFT) + thisW / 2) / thisW;
        sf.YScaleFp = ((otherH << REF_SCALE_SHIFT) + thisH / 2) / thisH;
        sf.XStepQ4 = (sf.XScaleFp + (1 << (REF_SCALE_SHIFT - SCALE_SUBPEL_BITS - 1))) >> (REF_SCALE_SHIFT - SCALE_SUBPEL_BITS);
        sf.YStepQ4 = (sf.YScaleFp + (1 << (REF_SCALE_SHIFT - SCALE_SUBPEL_BITS - 1))) >> (REF_SCALE_SHIFT - SCALE_SUBPEL_BITS);
        return sf;
    }

    public bool IsValid => XScaleFp != REF_INVALID_SCALE && YScaleFp != REF_INVALID_SCALE;
    /// <summary>av1_is_scaled.</summary>
    public bool IsScaled => IsValid && (XScaleFp != REF_NO_SCALE || YScaleFp != REF_NO_SCALE);

    private static int RoundPowerOfTwoSigned64(long value, int n)
        => (int)(value < 0 ? -((-value + (1L << (n - 1))) >> n) : (value + (1L << (n - 1))) >> n);

    /// <summary>av1_scaled_x.</summary>
    public int ScaledX(int val)
    {
        int off = (XScaleFp - (1 << REF_SCALE_SHIFT)) * (1 << (SUBPEL_BITS - 1));
        long tval = (long)val * XScaleFp + off;
        return RoundPowerOfTwoSigned64(tval, REF_SCALE_SHIFT - SCALE_EXTRA_BITS);
    }

    /// <summary>av1_scaled_y.</summary>
    public int ScaledY(int val)
    {
        int off = (YScaleFp - (1 << REF_SCALE_SHIFT)) * (1 << (SUBPEL_BITS - 1));
        long tval = (long)val * YScaleFp + off;
        return RoundPowerOfTwoSigned64(tval, REF_SCALE_SHIFT - SCALE_EXTRA_BITS);
    }

    /// <summary>av1_scale_mv: (row, col) in 1/1024 units.</summary>
    public (int Row, int Col) ScaleMv(AomMv mvq4, int x, int y)
    {
        int xOffQ4 = ScaledX(x << SUBPEL_BITS);
        int yOffQ4 = ScaledY(y << SUBPEL_BITS);
        return (ScaledY((y << SUBPEL_BITS) + mvq4.Row) - yOffQ4, ScaledX((x << SUBPEL_BITS) + mvq4.Col) - xOffQ4);
    }
}
