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

    // Running entropy estimate (Σ -log2(prob)) of everything encoded, for rate-distortion measurement. Matches
    // Av1CoeffEncode.SymBits/BoolBits, so a trial encode's MeasuredBits is a faithful coded-rate proxy. Only
    // accumulated when Measure is set, so the real (committed) encode path pays nothing.
    internal bool Measure;
    /// <summary>The coded bytes are never used (a row-parallel row, re-coded later by replaying its log): renormalisation
    /// keeps the coder state (and MeasuredBits) exact but stores no bytes.</summary>
    internal bool DiscardOutput;
    internal double MeasuredBits;
    private const double Log2_32768 = 15.0;

    /// <summary>Opaque snapshot of the coder state, so a trial (measurement) encode can be rolled back. The
    /// precarry buffer only grows during encoding, so restoring its length rewinds it exactly.</summary>
    internal readonly struct State
    {
        internal readonly ulong Low; internal readonly uint Rng; internal readonly int Cnt;
        internal readonly int PrecarryCount; internal readonly double Bits; internal readonly int LogCount;
        internal State(ulong low, uint rng, int cnt, int pc, double bits, int logCount)
        { Low = low; Rng = rng; Cnt = cnt; PrecarryCount = pc; Bits = bits; LogCount = logCount; }
    }

    internal State Save() => new(low, rng, cnt, precarry.Count, MeasuredBits, Log?.Count ?? 0);

    internal void Restore(in State s)
    {
        low = s.Low; rng = s.Rng; cnt = s.Cnt; MeasuredBits = s.Bits;
        if (precarry.Count > s.PrecarryCount) precarry.RemoveRange(s.PrecarryCount, precarry.Count - s.PrecarryCount);
        if (Log != null && Log.Count > s.LogCount) Log.RemoveRange(s.LogCount, Log.Count - s.LogCount);
    }

    /// <summary>One coded operation, for <see cref="Log"/>: a symbol (its interval fl/fh within an nsyms-ary alphabet),
    /// a bool with its Q15 probability, an equiprobable bool, or a <see cref="Mark"/> position.</summary>
    internal readonly struct LogOp
    {
        internal const byte Sym = 0, Bool = 1, Equi = 2, Marker = 3;
        // Symbolic forms (with SymIndex): adaptive symbol / bool on CDF array A at offset B, and the edge-partition
        // bool whose probability is gathered from partition CDF A/B at block level N (top or left gather).
        internal const byte SymC = 4, BoolC = 5, GatherTop = 6, GatherLeft = 7;
        internal readonly byte Kind, S, N; internal readonly int A, B;
        internal LogOp(byte kind, byte s, byte n, int a, int b) { Kind = kind; S = s; N = n; A = a; B = b; }
    }

    /// <summary>When set, every coded operation is also appended here (rolled back with Restore), so the tile can be
    /// re-coded later by <see cref="Replay"/> with extra syntax written at the <see cref="Mark"/> positions (syntax
    /// decided after the tile, e.g. loop-restoration units). Symbols are recorded as their coded intervals, so the
    /// replay is exact whatever the CDFs do afterwards.</summary>
    internal List<LogOp>? Log;

    /// <summary>With <see cref="Log"/>: record adaptive symbols by CDF identity (array id + offset in this index's
    /// context) instead of by interval, so <see cref="Replay"/> re-codes them against another context's CDFs (which
    /// adapt as they are replayed). Row-parallel encoding decides each superblock row on its own CDF copy this way.</summary>
    internal Av1CdfIndex? SymIndex;
    private bool suppressLog;

    /// <summary>Records a marker (no bits) at the current position of <see cref="Log"/>.</summary>
    internal void Mark(int id) => Log?.Add(new LogOp(LogOp.Marker, 0, 0, id, 0));

    internal List<LogOp> LogFrom(int start) => Log == null ? new() : Log.GetRange(start, Log.Count - start);

    internal void AppendLog(List<LogOp> tail) => Log?.AddRange(tail);

    /// <summary>Re-codes a recorded operation sequence into a fresh coder, calling <paramref name="onMarker"/> at each
    /// marker (it may code anything with the given writer) and returns the finished bytes. Without markers that write,
    /// the result equals the recording coder's own output.</summary>
    internal static byte[] Replay(List<LogOp> ops, Action<int, Av1MsacWriter>? onMarker, ushort[][]? cdfs = null)
    {
        var w = new Av1MsacWriter();
        w.ReplayInto(ops, onMarker, cdfs);
        return w.Finish();
    }

    /// <summary>Re-codes recorded operations into this coder. Symbolic ops use <paramref name="cdfs"/> (the arrays of
    /// the context they are replayed against, <see cref="Av1CdfIndex.Arrays"/> order), adapting them.</summary>
    internal void ReplayInto(List<LogOp> ops, Action<int, Av1MsacWriter>? onMarker, ushort[][]? cdfs)
    {
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case LogOp.Sym: EncodeInterval((uint)op.A, (uint)op.B, op.S, op.N); break;
                case LogOp.Bool: EncodeBool(op.S, (uint)op.A); break;
                case LogOp.Equi: EncodeBoolEqui(op.S); break;
                case LogOp.SymC: EncodeSymbolAdapt(cdfs![op.A].AsSpan(op.B), op.S, op.N); break;
                case LogOp.BoolC: EncodeBoolAdapt(cdfs![op.A].AsSpan(op.B), op.S); break;
                case LogOp.GatherTop: EncodeBool(op.S, Av1Decode.GatherTopPartitionProb(cdfs![op.A].AsSpan(op.B), (Av1BlockLevel)op.N)); break;
                case LogOp.GatherLeft: EncodeBool(op.S, Av1Decode.GatherLeftPartitionProb(cdfs![op.A].AsSpan(op.B), (Av1BlockLevel)op.N)); break;
                default: onMarker?.Invoke(op.A, this); break;
            }
        }
    }

    /// <summary>The edge-partition split bool (probability gathered from the partition CDF, non-adaptive).</summary>
    public void EncodeBoolGathered(ReadOnlySpan<ushort> partCdf, Av1BlockLevel bl, bool top, uint val)
    {
        uint f = top ? Av1Decode.GatherTopPartitionProb(partCdf, bl) : Av1Decode.GatherLeftPartitionProb(partCdf, bl);
        if (SymIndex != null && Log != null)
        {
            var (id, off) = SymIndex.Locate(partCdf);
            Log.Add(new LogOp(top ? LogOp.GatherTop : LogOp.GatherLeft, (byte)val, (byte)bl, id, off));
            suppressLog = true;
            EncodeBool(val, f);
            suppressLog = false;
        }
        else EncodeBool(val, f);
    }

    // Precarry-buffer bytes emitted since index `start` (a trial's output tail), so a winning trial's committed
    // state can be reconstructed without re-encoding. Used by true-RD to avoid re-running the winning subtree.
    internal int[] PrecarryFrom(int start) => DiscardOutput ? Array.Empty<int>() : precarry.GetRange(start, precarry.Count - start).ToArray();

    internal void AppendPrecarry(int[] tail) => precarry.AddRange(tail);

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
                if (!DiscardOutput) precarry.Add((int)(lo >> c));
                lo &= m;
                c -= 8;
                m >>= 8;
            }

            if (!DiscardOutput) precarry.Add((int)(lo >> c));
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
        => EncodeInterval(s > 0 ? icdf[s - 1] : (uint)(1 << 15), icdf[s], s, nsyms);

    // Codes symbol s of an nsyms-ary alphabet given its inverse-CDF interval [fh, fl).
    private void EncodeInterval(uint fl, uint fh, int s, int nsyms)
    {
        if (!suppressLog) Log?.Add(new LogOp(LogOp.Sym, (byte)s, (byte)nsyms, (int)fl, (int)fh));
        uint r = rng;
        if (Measure) MeasuredBits += Av1CoeffEncode.BitCost[Math.Clamp((int)fl - (int)fh, 0, 32768)];
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
        if (!suppressLog) Log?.Add(new LogOp(LogOp.Bool, (byte)val, 0, (int)f, 0));
        if (Measure) MeasuredBits += Av1CoeffEncode.BitCost[Math.Clamp((int)(val == 0 ? f : 32768 - f), 0, 32768)];
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
        Log?.Add(new LogOp(LogOp.Equi, (byte)val, 0, 0, 0));
        if (Measure) MeasuredBits += 1.0;
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
        if (SymIndex != null && Log != null)
        {
            var (id, off) = SymIndex.Locate(cdf);
            Log.Add(new LogOp(LogOp.BoolC, (byte)val, 0, id, off));
            suppressLog = true;
            EncodeBool(val, cdf[0]);
            suppressLog = false;
        }
        else EncodeBool(val, cdf[0]);
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
        // Pass the full cdf (not cdf[0..nsyms]) so the last symbol s==nsyms can read icdf[nsyms]. After adaptation
        // that slot holds the counter (<=32), and counter>>6 == 0, so v computes to 0 exactly as the decoder's
        // decode loop does (it likewise reads cdf[nsyms]) — the two stay in sync at the terminal symbol.
        if (SymIndex != null && Log != null)
        {
            var (id, off) = SymIndex.Locate(cdf);
            Log.Add(new LogOp(LogOp.SymC, (byte)s, (byte)nsyms, id, off));
            suppressLog = true;
            EncodeSymbol(cdf, s, nsyms);
            suppressLog = false;
        }
        else EncodeSymbol(cdf, s, nsyms);

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

    /// <summary>Inverse of Av1Msac.DecodeUniform: codes a value in [0,n) with a truncated-binary scheme —
    /// l = floor(log2 n)+1 bits, the first m = 2^l - n values in l-1 bits, the rest as (value+m) in l bits.</summary>
    public void EncodeUniform(uint value, uint n)
    {
        int l = 32 - System.Numerics.BitOperations.LeadingZeroCount(n);   // floor(log2 n)+1
        uint m = (1u << l) - n;
        if (value < m)
        {
            EncodeLiteral(value, l - 1);
        }
        else
        {
            EncodeLiteral(value + m, l);
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
