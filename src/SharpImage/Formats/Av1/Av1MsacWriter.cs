// AV1 entropy ENCODER (the "od_ec" daala range coder) — the write-side counterpart to the Av1Msac decoder
// (which is ported from dav1d). It produces a STANDARD AV1 bitstream: the decoder's `^0xFF` in Refill is
// dav1d's internal complement trick, not a format difference, so a faithful od_ec encoder round-trips with it.
//
// State mirrors libaom aom_dsp/entenc.c: a 64-bit `low` window, a 16-bit `rng`, and a bit counter `cnt`.
// Normalized bytes are staged in a "precarry" buffer whose entries can hold a 9th carry bit; Finish() resolves
// carries backward into the final byte stream. Verified bit-exact by encode→Av1Msac-decode round-trip tests.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SharpImage.Formats.Av1;

internal sealed class Av1MsacWriter
{
    private const int ProbShift = 6;
    private const int MinProb = 4;

    private ulong low;
    private uint rng = 0x8000;
    private int cnt = -9;
    private readonly List<int> precarry = new(1024); // each entry: byte value in low 8 bits, +carry in bit 8

    internal uint DbgRng => rng;
    internal ulong DbgLow => low;
    internal int DbgCnt => cnt;
    internal int DbgPrecarryCount => precarry.Count;

    // Bit length of a non-zero value (index of MSB + 1). rng is in [1, 65535].
    private static int ILogNz(uint x) => 32 - BitOperations.LeadingZeroCount(x);

    // Renormalize after an interval update, flushing whole bytes of `low` into the precarry buffer.
    // Transcribed from libaom od_ec_enc_normalize.
    private void Normalize(ulong lo, uint r)
    {
        int c = cnt;
        int d = 16 - ILogNz(r);
        int s = c + d;
        if (s >= 0)
        {
            c += 16;
            ulong m = (1UL << c) - 1;
            if (s >= 8)
            {
                precarry.Add((int)(lo >> c));
                lo &= m;
                c -= 8;
                m >>= 8;
            }

            precarry.Add((int)(lo >> c));
            s = c + d - 24;
            lo &= m;
        }

        low = lo << d;
        rng = r << d;
        cnt = s;
    }

    /// <summary>Encodes symbol <paramref name="s"/> of an <paramref name="nsyms"/>-ary alphabet whose inverse-
    /// cumulative CDF is <paramref name="icdf"/> (Q15, decreasing, terminal 0 implicit). Non-adaptive.</summary>
    public void EncodeSymbol(ReadOnlySpan<ushort> icdf, int s, int nsyms)
    {
        uint r = rng;
        uint fl = s > 0 ? icdf[s - 1] : (uint)(1 << 15);
        uint fh = icdf[s];
        ulong l = low;
        if (fl < (1 << 15))
        {
            uint u = (((r >> 8) * (fl >> ProbShift)) >> (7 - ProbShift)) + (uint)(MinProb * (nsyms - (s - 1)));
            uint v = (((r >> 8) * (fh >> ProbShift)) >> (7 - ProbShift)) + (uint)(MinProb * (nsyms - s));
            l += r - u;
            r = u - v;
        }
        else
        {
            uint v = (((r >> 8) * (fh >> ProbShift)) >> (7 - ProbShift)) + (uint)(MinProb * (nsyms - s));
            r -= v;
        }

        Normalize(l, r);
    }

    /// <summary>Encodes a boolean <paramref name="val"/> where <paramref name="f"/> is the Q15 probability of 0
    /// (matches the decoder's DecodeBool). Non-adaptive.</summary>
    public void EncodeBool(uint val, uint f)
    {
        uint r = rng;
        ulong l = low;
        uint v = (((r >> 8) * (f >> ProbShift)) >> (7 - ProbShift)) + MinProb;
        if (val != 0)
        {
            l += r - v;
            r = v;
        }
        else
        {
            r -= v;
        }

        Normalize(l, r);
    }

    /// <summary>Encodes a 50/50 boolean (matches DecodeBoolEqui).</summary>
    public void EncodeBoolEqui(uint val)
    {
        uint r = rng;
        ulong l = low;
        uint v = ((r >> 8) << 7) + MinProb;
        if (val != 0)
        {
            l += r - v;
            r = v;
        }
        else
        {
            r -= v;
        }

        Normalize(l, r);
    }

    /// <summary>Adaptive boolean: encodes with cdf[0] (Q15 prob of 0) then updates the CDF exactly as the
    /// decoder's DecodeBoolAdapt does.</summary>
    public void EncodeBoolAdapt(Span<ushort> cdf, uint val)
    {
        EncodeBool(val, cdf[0]);
        uint count = cdf[1];
        int rate = 4 + (int)(count >> 4);
        if (val != 0)
        {
            cdf[0] += (ushort)((32768 - cdf[0]) >> rate);
        }
        else
        {
            cdf[0] -= (ushort)(cdf[0] >> rate);
        }

        cdf[1] = (ushort)(count + (count < 32 ? 1u : 0u));
    }

    /// <summary>Adaptive symbol: cdf holds the inverse-cumulative CDF (nsyms entries, cdf[nsyms-1]=0) plus a
    /// trailing adaptation counter at cdf[nsyms]. Encodes symbol s and updates the CDF like DecodeSymbolAdapt.</summary>
    public void EncodeSymbolAdapt(Span<ushort> cdf, int s, int nsyms)
    {
        EncodeSymbol(cdf.Slice(0, nsyms), s, nsyms);

        uint count = cdf[nsyms];
        int rate = 4 + (int)(count >> 4) + (nsyms > 2 ? 1 : 0);
        int i = 0;
        for (; i < s; i++)
        {
            cdf[i] += (ushort)((32768 - cdf[i]) >> rate);
        }

        for (; i < nsyms; i++)
        {
            cdf[i] -= (ushort)(cdf[i] >> rate);
        }

        cdf[nsyms] = (ushort)(count + (count < 32 ? 1u : 0u));
    }

    /// <summary>Encodes <paramref name="nbits"/> raw bits (MSB first) as equiprobable bools (od_ec_enc_bits).</summary>
    public void EncodeLiteral(uint value, int nbits)
    {
        for (int i = nbits - 1; i >= 0; i--)
        {
            EncodeBoolEqui((value >> i) & 1u);
        }
    }

    /// <summary>Flushes the coder and returns the finished byte stream (carries resolved).</summary>
    public byte[] Finish()
    {
        // Emit enough of `low` to uniquely place a value inside the final interval, then flush the window.
        // libaom od_ec_enc_done: round `low` up to a value whose trailing bits are free within the interval.
        ulong l = low;
        int c = cnt;
        int s = 10 + c;
        ulong m = 0x3FFF;
        ulong e = ((l + m) & ~m) | (m + 1);
        if (s > 0)
        {
            ulong n = (1UL << (c + 16)) - 1;
            do
            {
                precarry.Add((int)(e >> (c + 16)));
                e &= n;
                s -= 8;
                c -= 8;
                n >>= 8;
            }
            while (s > 0);
        }

        // Resolve carries backward: an entry >= 256 carries 1 into the previous byte.
        int count = precarry.Count;
        var outBytes = new byte[count];
        int carry = 0;
        for (int i = count - 1; i >= 0; i--)
        {
            int v = precarry[i] + carry;
            outBytes[i] = (byte)v;
            carry = v >> 8;
        }

        return outBytes;
    }
}
