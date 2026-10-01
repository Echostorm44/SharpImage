using System;

namespace SharpImage.Formats.Av1;

/// <summary>Port of libaom aom_dsp/loopfilter.c's high bit depth edge filters (aom_highbd_lpf_{horizontal,vertical}_
/// {4,6,8,14}_c): the 8-bit thresholds scaled by 2^(bd - 8), the filter4 arithmetic in the bit depth's signed range.
/// libaom's SSE2 / AVX2 highbd kernels (single and _dual) compute the C's values line by line, so one call filters 4
/// lines here and the dual / grouped variants are repeated calls.</summary>
internal static partial class AomLpf
{
    private static int SccHigh(int t, int bd) => bd switch
    {
        10 => Math.Clamp(t, -128 * 4, 128 * 4 - 1),
        12 => Math.Clamp(t, -128 * 16, 128 * 16 - 1),
        _ => Math.Clamp(t, -128, 127),
    };

    private static void HighbdFilter4(int mask, int thresh, ushort[] b, int s, int a, int bd)
    {
        int shift = bd - 8;
        int p1 = b[s - 2 * a], p0 = b[s - a], q0 = b[s], q1 = b[s + a];
        int ps1 = (short)(p1 - (0x80 << shift)), ps0 = (short)(p0 - (0x80 << shift));
        int qs0 = (short)(q0 - (0x80 << shift)), qs1 = (short)(q1 - (0x80 << shift));
        int thresh16 = thresh << shift;
        int hev = (Math.Abs(p1 - p0) > thresh16 || Math.Abs(q1 - q0) > thresh16) ? -1 : 0;
        int filter = SccHigh(ps1 - qs1, bd) & hev;
        filter = SccHigh(filter + 3 * (qs0 - ps0), bd) & mask;
        int filter1 = SccHigh(filter + 4, bd) >> 3;
        int filter2 = SccHigh(filter + 3, bd) >> 3;
        b[s] = (ushort)(SccHigh(qs0 - filter1, bd) + (0x80 << shift));
        b[s - a] = (ushort)(SccHigh(ps0 + filter2, bd) + (0x80 << shift));
        filter = ((filter1 + 1) >> 1) & ~hev;
        b[s + a] = (ushort)(SccHigh(qs1 - filter, bd) + (0x80 << shift));
        b[s - 2 * a] = (ushort)(SccHigh(ps1 + filter, bd) + (0x80 << shift));
    }

    private static int HighbdMask(int limit16, int blimit16, int p3, int p2, int p1, int p0, int q0, int q1, int q2, int q3)
    {
        bool bad = Math.Abs(p3 - p2) > limit16 || Math.Abs(p2 - p1) > limit16 || Math.Abs(p1 - p0) > limit16 ||
                   Math.Abs(q1 - q0) > limit16 || Math.Abs(q2 - q1) > limit16 || Math.Abs(q3 - q2) > limit16 ||
                   Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > blimit16;
        return bad ? 0 : -1;
    }

    private static bool HighbdFlat4(int t16, int p3, int p2, int p1, int p0, int q0, int q1, int q2, int q3)
        => !(Math.Abs(p1 - p0) > t16 || Math.Abs(q1 - q0) > t16 || Math.Abs(p2 - p0) > t16 || Math.Abs(q2 - q0) > t16 ||
             Math.Abs(p3 - p0) > t16 || Math.Abs(q3 - q0) > t16);

    /// <summary>The high bit depth filter for a filter_length (4, 6, 8, 14) over 4 lines; 0 does nothing.</summary>
    public static void ApplyHighbd(int length, ushort[] b, int s, int across, int along, int blimit, int limit, int thresh, int bd)
    {
        int sh = bd - 8, a = across;
        int limit16 = limit << sh, blimit16 = blimit << sh, one16 = 1 << sh;
        for (int i = 0; i < 4; i++, s += along)
        {
            switch (length)
            {
                case 4:
                {
                    int p1 = b[s - 2 * a], p0 = b[s - a], q0 = b[s], q1 = b[s + a];
                    int mask = Math.Abs(p1 - p0) > limit16 || Math.Abs(q1 - q0) > limit16 ||
                        Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > blimit16 ? 0 : -1;
                    HighbdFilter4(mask, thresh, b, s, a, bd);
                    break;
                }
                case 6:
                {
                    int p2 = b[s - 3 * a], p1 = b[s - 2 * a], p0 = b[s - a], q0 = b[s], q1 = b[s + a], q2 = b[s + 2 * a];
                    int mask = Math.Abs(p2 - p1) > limit16 || Math.Abs(p1 - p0) > limit16 || Math.Abs(q1 - q0) > limit16 ||
                        Math.Abs(q2 - q1) > limit16 || Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > blimit16 ? 0 : -1;
                    bool flat = !(Math.Abs(p1 - p0) > one16 || Math.Abs(q1 - q0) > one16 || Math.Abs(p2 - p0) > one16 || Math.Abs(q2 - q0) > one16);
                    if (flat && mask != 0)
                    {
                        b[s - 2 * a] = (ushort)((p2 * 3 + p1 * 2 + p0 * 2 + q0 + 4) >> 3);
                        b[s - a] = (ushort)((p2 + p1 * 2 + p0 * 2 + q0 * 2 + q1 + 4) >> 3);
                        b[s] = (ushort)((p1 + p0 * 2 + q0 * 2 + q1 * 2 + q2 + 4) >> 3);
                        b[s + a] = (ushort)((p0 + q0 * 2 + q1 * 2 + q2 * 3 + 4) >> 3);
                    }
                    else HighbdFilter4(mask, thresh, b, s, a, bd);
                    break;
                }
                case 8:
                {
                    int p3 = b[s - 4 * a], p2 = b[s - 3 * a], p1 = b[s - 2 * a], p0 = b[s - a];
                    int q0 = b[s], q1 = b[s + a], q2 = b[s + 2 * a], q3 = b[s + 3 * a];
                    int mask = HighbdMask(limit16, blimit16, p3, p2, p1, p0, q0, q1, q2, q3);
                    bool flat = HighbdFlat4(one16, p3, p2, p1, p0, q0, q1, q2, q3);
                    if (flat && mask != 0) Filter8Apply(b, s, a, p3, p2, p1, p0, q0, q1, q2, q3);
                    else HighbdFilter4(mask, thresh, b, s, a, bd);
                    break;
                }
                case 14:
                {
                    int p6 = b[s - 7 * a], p5 = b[s - 6 * a], p4 = b[s - 5 * a], p3 = b[s - 4 * a], p2 = b[s - 3 * a],
                        p1 = b[s - 2 * a], p0 = b[s - a];
                    int q0 = b[s], q1 = b[s + a], q2 = b[s + 2 * a], q3 = b[s + 3 * a], q4 = b[s + 4 * a], q5 = b[s + 5 * a],
                        q6 = b[s + 6 * a];
                    int mask = HighbdMask(limit16, blimit16, p3, p2, p1, p0, q0, q1, q2, q3);
                    bool flat = HighbdFlat4(one16, p3, p2, p1, p0, q0, q1, q2, q3);
                    bool flat2 = HighbdFlat4(one16, p6, p5, p4, p0, q0, q4, q5, q6);
                    if (flat2 && flat && mask != 0)
                    {
                        b[s - 6 * a] = (ushort)((p6 * 7 + p5 * 2 + p4 * 2 + p3 + p2 + p1 + p0 + q0 + 8) >> 4);
                        b[s - 5 * a] = (ushort)((p6 * 5 + p5 * 2 + p4 * 2 + p3 * 2 + p2 + p1 + p0 + q0 + q1 + 8) >> 4);
                        b[s - 4 * a] = (ushort)((p6 * 4 + p5 + p4 * 2 + p3 * 2 + p2 * 2 + p1 + p0 + q0 + q1 + q2 + 8) >> 4);
                        b[s - 3 * a] = (ushort)((p6 * 3 + p5 + p4 + p3 * 2 + p2 * 2 + p1 * 2 + p0 + q0 + q1 + q2 + q3 + 8) >> 4);
                        b[s - 2 * a] = (ushort)((p6 * 2 + p5 + p4 + p3 + p2 * 2 + p1 * 2 + p0 * 2 + q0 + q1 + q2 + q3 + q4 + 8) >> 4);
                        b[s - a] = (ushort)((p6 + p5 + p4 + p3 + p2 + p1 * 2 + p0 * 2 + q0 * 2 + q1 + q2 + q3 + q4 + q5 + 8) >> 4);
                        b[s] = (ushort)((p5 + p4 + p3 + p2 + p1 + p0 * 2 + q0 * 2 + q1 * 2 + q2 + q3 + q4 + q5 + q6 + 8) >> 4);
                        b[s + a] = (ushort)((p4 + p3 + p2 + p1 + p0 + q0 * 2 + q1 * 2 + q2 * 2 + q3 + q4 + q5 + q6 * 2 + 8) >> 4);
                        b[s + 2 * a] = (ushort)((p3 + p2 + p1 + p0 + q0 + q1 * 2 + q2 * 2 + q3 * 2 + q4 + q5 + q6 * 3 + 8) >> 4);
                        b[s + 3 * a] = (ushort)((p2 + p1 + p0 + q0 + q1 + q2 * 2 + q3 * 2 + q4 * 2 + q5 + q6 * 4 + 8) >> 4);
                        b[s + 4 * a] = (ushort)((p1 + p0 + q0 + q1 + q2 + q3 * 2 + q4 * 2 + q5 * 2 + q6 * 5 + 8) >> 4);
                        b[s + 5 * a] = (ushort)((p0 + q0 + q1 + q2 + q3 + q4 * 2 + q5 * 2 + q6 * 7 + 8) >> 4);
                    }
                    else if (flat && mask != 0) Filter8Apply(b, s, a, p3, p2, p1, p0, q0, q1, q2, q3);
                    else HighbdFilter4(mask, thresh, b, s, a, bd);
                    break;
                }
                default: return;
            }
        }
    }

    private static void Filter8Apply(ushort[] b, int s, int a, int p3, int p2, int p1, int p0, int q0, int q1, int q2, int q3)
    {
        b[s - 3 * a] = (ushort)((p3 + p3 + p3 + 2 * p2 + p1 + p0 + q0 + 4) >> 3);
        b[s - 2 * a] = (ushort)((p3 + p3 + p2 + 2 * p1 + p0 + q0 + q1 + 4) >> 3);
        b[s - a] = (ushort)((p3 + p2 + p1 + 2 * p0 + q0 + q1 + q2 + 4) >> 3);
        b[s] = (ushort)((p2 + p1 + p0 + 2 * q0 + q1 + q2 + q3 + 4) >> 3);
        b[s + a] = (ushort)((p1 + p0 + q0 + 2 * q1 + q2 + q3 + q3 + 4) >> 3);
        b[s + 2 * a] = (ushort)((p0 + q0 + q1 + 2 * q2 + q3 + q3 + q3 + 4) >> 3);
    }
}
