using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>Port of libaom aom_dsp/loopfilter.c (8-bit): the 4/6/8/14-tap deblocking edge filters. One call filters
/// 4 lines of an edge, like aom_lpf_{horizontal,vertical}_N_c; the dual/quad variants are 2/4 consecutive calls. The
/// scalar reference and non-AVX2 fallback of the vectorized kernels in AomLpf.Simd.cs. across = the sample step across
/// the edge (pitch for a horizontal edge, 1 for a vertical one), along = the step to the next line.</summary>
internal static partial class AomLpf
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static sbyte Scc(int t) => (sbyte)(t < -128 ? -128 : t > 127 ? 127 : t);   // signed_char_clamp

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Gt(int a, int limit) => a > limit ? -1 : 0;

    // should we apply any filter at all: -1 yes, 0 no
    private static sbyte FilterMask2(int limit, int blimit, int p1, int p0, int q0, int q1)
    {
        int mask = 0;
        mask |= Gt(Math.Abs(p1 - p0), limit);
        mask |= Gt(Math.Abs(q1 - q0), limit);
        mask |= Gt(Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2, blimit);
        return (sbyte)~mask;
    }

    private static sbyte FilterMask(int limit, int blimit, int p3, int p2, int p1, int p0, int q0, int q1, int q2, int q3)
    {
        int mask = 0;
        mask |= Gt(Math.Abs(p3 - p2), limit);
        mask |= Gt(Math.Abs(p2 - p1), limit);
        mask |= Gt(Math.Abs(p1 - p0), limit);
        mask |= Gt(Math.Abs(q1 - q0), limit);
        mask |= Gt(Math.Abs(q2 - q1), limit);
        mask |= Gt(Math.Abs(q3 - q2), limit);
        mask |= Gt(Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2, blimit);
        return (sbyte)~mask;
    }

    private static sbyte FilterMask3Chroma(int limit, int blimit, int p2, int p1, int p0, int q0, int q1, int q2)
    {
        int mask = 0;
        mask |= Gt(Math.Abs(p2 - p1), limit);
        mask |= Gt(Math.Abs(p1 - p0), limit);
        mask |= Gt(Math.Abs(q1 - q0), limit);
        mask |= Gt(Math.Abs(q2 - q1), limit);
        mask |= Gt(Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2, blimit);
        return (sbyte)~mask;
    }

    private static sbyte FlatMask3Chroma(int thresh, int p2, int p1, int p0, int q0, int q1, int q2)
    {
        int mask = 0;
        mask |= Gt(Math.Abs(p1 - p0), thresh);
        mask |= Gt(Math.Abs(q1 - q0), thresh);
        mask |= Gt(Math.Abs(p2 - p0), thresh);
        mask |= Gt(Math.Abs(q2 - q0), thresh);
        return (sbyte)~mask;
    }

    private static sbyte FlatMask4(int thresh, int p3, int p2, int p1, int p0, int q0, int q1, int q2, int q3)
    {
        int mask = 0;
        mask |= Gt(Math.Abs(p1 - p0), thresh);
        mask |= Gt(Math.Abs(q1 - q0), thresh);
        mask |= Gt(Math.Abs(p2 - p0), thresh);
        mask |= Gt(Math.Abs(q2 - q0), thresh);
        mask |= Gt(Math.Abs(p3 - p0), thresh);
        mask |= Gt(Math.Abs(q3 - q0), thresh);
        return (sbyte)~mask;
    }

    private static sbyte HevMask(int thresh, int p1, int p0, int q0, int q1)
    {
        int hev = 0;
        hev |= Gt(Math.Abs(p1 - p0), thresh);
        hev |= Gt(Math.Abs(q1 - q0), thresh);
        return (sbyte)hev;
    }

    // filter4 on the samples at s - 2a, s - a, s, s + a
    private static void Filter4(sbyte mask, int thresh, byte[] b, int s, int a)
    {
        sbyte ps1 = (sbyte)(b[s - 2 * a] ^ 0x80);
        sbyte ps0 = (sbyte)(b[s - a] ^ 0x80);
        sbyte qs0 = (sbyte)(b[s] ^ 0x80);
        sbyte qs1 = (sbyte)(b[s + a] ^ 0x80);
        sbyte hev = HevMask(thresh, b[s - 2 * a], b[s - a], b[s], b[s + a]);

        // add outer taps if we have high edge variance
        sbyte filter = (sbyte)(Scc(ps1 - qs1) & hev);
        // inner taps
        filter = (sbyte)(Scc(filter + 3 * (qs0 - ps0)) & mask);
        // save bottom 3 bits so that we round one side +4 and the other +3
        sbyte filter1 = (sbyte)(Scc(filter + 4) >> 3);
        sbyte filter2 = (sbyte)(Scc(filter + 3) >> 3);

        b[s] = (byte)(Scc(qs0 - filter1) ^ 0x80);
        b[s - a] = (byte)(Scc(ps0 + filter2) ^ 0x80);

        // outer tap adjustments
        filter = (sbyte)(((filter1 + 1) >> 1) & ~hev);

        b[s + a] = (byte)(Scc(qs1 - filter) ^ 0x80);
        b[s - 2 * a] = (byte)(Scc(ps1 + filter) ^ 0x80);
    }

    private static void Filter6(sbyte mask, int thresh, sbyte flat, byte[] b, int s, int a)
    {
        if (flat != 0 && mask != 0)
        {
            int p2 = b[s - 3 * a], p1 = b[s - 2 * a], p0 = b[s - a];
            int q0 = b[s], q1 = b[s + a], q2 = b[s + 2 * a];
            // 5-tap filter [1, 2, 2, 2, 1]
            b[s - 2 * a] = (byte)((p2 * 3 + p1 * 2 + p0 * 2 + q0 + 4) >> 3);
            b[s - a] = (byte)((p2 + p1 * 2 + p0 * 2 + q0 * 2 + q1 + 4) >> 3);
            b[s] = (byte)((p1 + p0 * 2 + q0 * 2 + q1 * 2 + q2 + 4) >> 3);
            b[s + a] = (byte)((p0 + q0 * 2 + q1 * 2 + q2 * 3 + 4) >> 3);
        }
        else
        {
            Filter4(mask, thresh, b, s, a);
        }
    }

    private static void Filter8(sbyte mask, int thresh, sbyte flat, byte[] b, int s, int a)
    {
        if (flat != 0 && mask != 0)
        {
            int p3 = b[s - 4 * a], p2 = b[s - 3 * a], p1 = b[s - 2 * a], p0 = b[s - a];
            int q0 = b[s], q1 = b[s + a], q2 = b[s + 2 * a], q3 = b[s + 3 * a];
            // 7-tap filter [1, 1, 1, 2, 1, 1, 1]
            b[s - 3 * a] = (byte)((p3 + p3 + p3 + 2 * p2 + p1 + p0 + q0 + 4) >> 3);
            b[s - 2 * a] = (byte)((p3 + p3 + p2 + 2 * p1 + p0 + q0 + q1 + 4) >> 3);
            b[s - a] = (byte)((p3 + p2 + p1 + 2 * p0 + q0 + q1 + q2 + 4) >> 3);
            b[s] = (byte)((p2 + p1 + p0 + 2 * q0 + q1 + q2 + q3 + 4) >> 3);
            b[s + a] = (byte)((p1 + p0 + q0 + 2 * q1 + q2 + q3 + q3 + 4) >> 3);
            b[s + 2 * a] = (byte)((p0 + q0 + q1 + 2 * q2 + q3 + q3 + q3 + 4) >> 3);
        }
        else
        {
            Filter4(mask, thresh, b, s, a);
        }
    }

    private static void Filter14(sbyte mask, int thresh, sbyte flat, sbyte flat2, byte[] b, int s, int a)
    {
        if (flat2 != 0 && flat != 0 && mask != 0)
        {
            int p6 = b[s - 7 * a], p5 = b[s - 6 * a], p4 = b[s - 5 * a], p3 = b[s - 4 * a], p2 = b[s - 3 * a],
                p1 = b[s - 2 * a], p0 = b[s - a];
            int q0 = b[s], q1 = b[s + a], q2 = b[s + 2 * a], q3 = b[s + 3 * a], q4 = b[s + 4 * a], q5 = b[s + 5 * a],
                q6 = b[s + 6 * a];
            // 13-tap filter [1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1]
            b[s - 6 * a] = (byte)((p6 * 7 + p5 * 2 + p4 * 2 + p3 + p2 + p1 + p0 + q0 + 8) >> 4);
            b[s - 5 * a] = (byte)((p6 * 5 + p5 * 2 + p4 * 2 + p3 * 2 + p2 + p1 + p0 + q0 + q1 + 8) >> 4);
            b[s - 4 * a] = (byte)((p6 * 4 + p5 + p4 * 2 + p3 * 2 + p2 * 2 + p1 + p0 + q0 + q1 + q2 + 8) >> 4);
            b[s - 3 * a] = (byte)((p6 * 3 + p5 + p4 + p3 * 2 + p2 * 2 + p1 * 2 + p0 + q0 + q1 + q2 + q3 + 8) >> 4);
            b[s - 2 * a] = (byte)((p6 * 2 + p5 + p4 + p3 + p2 * 2 + p1 * 2 + p0 * 2 + q0 + q1 + q2 + q3 + q4 + 8) >> 4);
            b[s - a] = (byte)((p6 + p5 + p4 + p3 + p2 + p1 * 2 + p0 * 2 + q0 * 2 + q1 + q2 + q3 + q4 + q5 + 8) >> 4);
            b[s] = (byte)((p5 + p4 + p3 + p2 + p1 + p0 * 2 + q0 * 2 + q1 * 2 + q2 + q3 + q4 + q5 + q6 + 8) >> 4);
            b[s + a] = (byte)((p4 + p3 + p2 + p1 + p0 + q0 * 2 + q1 * 2 + q2 * 2 + q3 + q4 + q5 + q6 * 2 + 8) >> 4);
            b[s + 2 * a] = (byte)((p3 + p2 + p1 + p0 + q0 + q1 * 2 + q2 * 2 + q3 * 2 + q4 + q5 + q6 * 3 + 8) >> 4);
            b[s + 3 * a] = (byte)((p2 + p1 + p0 + q0 + q1 + q2 * 2 + q3 * 2 + q4 * 2 + q5 + q6 * 4 + 8) >> 4);
            b[s + 4 * a] = (byte)((p1 + p0 + q0 + q1 + q2 + q3 * 2 + q4 * 2 + q5 * 2 + q6 * 5 + 8) >> 4);
            b[s + 5 * a] = (byte)((p0 + q0 + q1 + q2 + q3 + q4 * 2 + q5 * 2 + q6 * 7 + 8) >> 4);
        }
        else
        {
            Filter8(mask, thresh, flat, b, s, a);
        }
    }

    /// <summary>aom_lpf_{horizontal,vertical}_4_c.</summary>
    public static void Lpf4(byte[] b, int s, int across, int along, int blimit, int limit, int thresh)
    {
        for (int i = 0; i < 4; i++, s += along)
        {
            sbyte mask = FilterMask2(limit, blimit, b[s - 2 * across], b[s - across], b[s], b[s + across]);
            Filter4(mask, thresh, b, s, across);
        }
    }

    /// <summary>aom_lpf_{horizontal,vertical}_6_c (chroma).</summary>
    public static void Lpf6(byte[] b, int s, int across, int along, int blimit, int limit, int thresh)
    {
        int a = across;
        for (int i = 0; i < 4; i++, s += along)
        {
            int p2 = b[s - 3 * a], p1 = b[s - 2 * a], p0 = b[s - a], q0 = b[s], q1 = b[s + a], q2 = b[s + 2 * a];
            sbyte mask = FilterMask3Chroma(limit, blimit, p2, p1, p0, q0, q1, q2);
            sbyte flat = FlatMask3Chroma(1, p2, p1, p0, q0, q1, q2);
            Filter6(mask, thresh, flat, b, s, a);
        }
    }

    /// <summary>aom_lpf_{horizontal,vertical}_8_c.</summary>
    public static void Lpf8(byte[] b, int s, int across, int along, int blimit, int limit, int thresh)
    {
        int a = across;
        for (int i = 0; i < 4; i++, s += along)
        {
            int p3 = b[s - 4 * a], p2 = b[s - 3 * a], p1 = b[s - 2 * a], p0 = b[s - a];
            int q0 = b[s], q1 = b[s + a], q2 = b[s + 2 * a], q3 = b[s + 3 * a];
            sbyte mask = FilterMask(limit, blimit, p3, p2, p1, p0, q0, q1, q2, q3);
            sbyte flat = FlatMask4(1, p3, p2, p1, p0, q0, q1, q2, q3);
            Filter8(mask, thresh, flat, b, s, a);
        }
    }

    /// <summary>aom_lpf_{horizontal,vertical}_14_c (mb_lpf_*_edge_w, 4 lines).</summary>
    public static void Lpf14(byte[] b, int s, int across, int along, int blimit, int limit, int thresh)
    {
        int a = across;
        for (int i = 0; i < 4; i++, s += along)
        {
            int p6 = b[s - 7 * a], p5 = b[s - 6 * a], p4 = b[s - 5 * a], p3 = b[s - 4 * a], p2 = b[s - 3 * a],
                p1 = b[s - 2 * a], p0 = b[s - a];
            int q0 = b[s], q1 = b[s + a], q2 = b[s + 2 * a], q3 = b[s + 3 * a], q4 = b[s + 4 * a], q5 = b[s + 5 * a],
                q6 = b[s + 6 * a];
            sbyte mask = FilterMask(limit, blimit, p3, p2, p1, p0, q0, q1, q2, q3);
            sbyte flat = FlatMask4(1, p3, p2, p1, p0, q0, q1, q2, q3);
            sbyte flat2 = FlatMask4(1, p6, p5, p4, p0, q0, q4, q5, q6);
            Filter14(mask, thresh, flat, flat2, b, s, a);
        }
    }

    /// <summary>The filter for a filter_length (4, 6, 8, 14) over 4 lines; 0 does nothing.</summary>
    public static void Apply(int length, byte[] b, int s, int across, int along, int blimit, int limit, int thresh)
    {
        switch (length)
        {
            case 4: Lpf4(b, s, across, along, blimit, limit, thresh); break;
            case 6: Lpf6(b, s, across, along, blimit, limit, thresh); break;
            case 8: Lpf8(b, s, across, along, blimit, limit, thresh); break;
            case 14: Lpf14(b, s, across, along, blimit, limit, thresh); break;
        }
    }
}
