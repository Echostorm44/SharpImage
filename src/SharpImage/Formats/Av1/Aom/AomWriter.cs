using System;
using System.Numerics;

namespace SharpImage.Formats.Av1;

/// <summary>Port of libaom 3.14.1 aom_dsp/entenc.c (od_ec_enc: the daala range encoder with its batched, carry-
/// propagating byte flush and od_ec_enc_done termination) and aom_dsp/bitwriter.{h,c} (aom_writer: aom_write_symbol /
/// aom_write_cdf / aom_write / aom_write_bit / aom_write_literal with update_cdf adaptation). CDFs are SharpImage's
/// dav1d-layout arrays: the same inverse-CDF values as libaom's, except that the adaptation counter lives at
/// cdf[nsymbs - 1] where libaom keeps its terminal AOM_ICDF(CDF_PROB_TOP) = 0 (and its counter at cdf[nsymbs]);
/// the coder reads that terminal as 0.</summary>
internal sealed class AomWriter
{
    private const int EcProbShift = 6, EcMinProb = 4, CdfProbTop = 32768;

    // od_ec_enc
    private byte[] _buf;
    private uint _offs;
    private ulong _low;
    private uint _rng = 0x8000;
    private int _cnt = -9;

    /// <summary>aom_writer.allow_update_cdf.</summary>
    public bool AllowUpdateCdf = true;

    /// <summary>Optional symbol trace (kind, value, cdf values / probability, nsyms) for first-difference hunting.</summary>
    public System.IO.TextWriter? Trace;

    /// <summary>aom_start_encode: od_ec_enc_init(&amp;w->ec, 62025).</summary>
    public AomWriter(int initialSize = 62025) { _buf = new byte[initialSize]; }

    /// <summary>od_ec_enc_tell (aom_tell_size): the bits written so far plus one reserved for termination.</summary>
    public int TellBits => (_cnt + 10) + (int)_offs * 8;

    private static int ILogNz(uint x) => 32 - BitOperations.LeadingZeroCount(x);

    private static void PropagateCarryBwd(byte[] buf, uint offs)
    {
        int carry;
        do
        {
            int sum = buf[offs] + 1;
            buf[offs--] = (byte)sum;
            carry = sum >> 8;
        } while (carry != 0);
    }

    /// <summary>od_ec_enc_normalize (flushes whole bytes once 40 or more bits are pending).</summary>
    private void Normalize(ulong low, uint rng)
    {
        int c = _cnt;
        int d = 16 - ILogNz(rng);
        int s = c + d;
        if (s >= 40)
        {
            if (_offs + 8 > _buf.Length) Array.Resize(ref _buf, 2 * _buf.Length + 8);
            int numBytesReady = (s >> 3) + 1;
            c += 24 - (numBytesReady << 3);
            ulong output = low >> c;
            low &= (1UL << c) - 1;
            ulong mask = 1UL << (numBytesReady << 3);
            ulong carry = output & mask;
            mask -= 1;
            output &= mask;
            // write_enc_data_to_out_buf: the ready bytes big-endian at offs, then the carry into the bytes before
            ulong reg = output << ((8 - numBytesReady) << 3);
            for (int i = 0; i < 8; i++) _buf[_offs + i] = (byte)(reg >> (56 - 8 * i));
            if (carry != 0) PropagateCarryBwd(_buf, _offs - 1);
            _offs += (uint)numBytesReady;
            s = c + d - 24;
        }
        _low = low << d;
        _rng = rng << d;
        _cnt = s;
    }

    /// <summary>od_ec_encode_q15: fl / fh are CDF_PROB_TOP minus the cumulative frequency below / through s.</summary>
    private void EncodeQ15(uint fl, uint fh, int s, int nsyms)
    {
        ulong l = _low;
        uint r = _rng;
        int n = nsyms - 1;
        if (fl < CdfProbTop)
        {
            uint u = (((r >> 8) * (fl >> EcProbShift)) >> (7 - EcProbShift)) + (uint)(EcMinProb * (n - (s - 1)));
            uint v = (((r >> 8) * (fh >> EcProbShift)) >> (7 - EcProbShift)) + (uint)(EcMinProb * (n - (s + 0)));
            l += r - u;
            r = u - v;
        }
        else
        {
            r -= (((r >> 8) * (fh >> EcProbShift)) >> (7 - EcProbShift)) + (uint)(EcMinProb * (n - (s + 0)));
        }
        Normalize(l, r);
    }

    /// <summary>od_ec_encode_bool_q15: f is the probability (Q15) that val is one.</summary>
    private void EncodeBoolQ15(int val, uint f)
    {
        ulong l = _low;
        uint r = _rng;
        uint v = ((r >> 8) * (f >> EcProbShift)) >> (7 - EcProbShift);
        v += EcMinProb;
        if (val != 0) l += r - v;
        r = val != 0 ? v : r - v;
        Normalize(l, r);
    }

    /// <summary>The inverse CDF value at i of a dav1d-layout CDF of nsymbs symbols (the terminal entry is 0).</summary>
    private static uint Icdf(ReadOnlySpan<ushort> cdf, int i, int nsymbs) => i >= nsymbs - 1 ? 0u : cdf[i];

    /// <summary>aom_write_cdf (od_ec_encode_cdf_q15): codes s with a non-adaptive dav1d-layout CDF.</summary>
    public void WriteCdf(int s, ReadOnlySpan<ushort> cdf, int nsymbs)
    {
        if (Trace != null) TraceSym('c', s, cdf, nsymbs);
        EncodeQ15(s > 0 ? Icdf(cdf, s - 1, nsymbs) : CdfProbTop, Icdf(cdf, s, nsymbs), s, nsymbs);
    }

    /// <summary>aom_write_symbol: aom_write_cdf then update_cdf when allowed.</summary>
    public void WriteSymbol(int s, ushort[] cdf, int nsymbs)
    {
        if (Trace != null) TraceSym('s', s, cdf, nsymbs);
        EncodeQ15(s > 0 ? Icdf(cdf, s - 1, nsymbs) : CdfProbTop, Icdf(cdf, s, nsymbs), s, nsymbs);
        if (AllowUpdateCdf) AomCdf.Update(cdf, s, nsymbs);
    }

    /// <summary>aom_write: bit with an 8-bit probability (of zero).</summary>
    public void Write(int bit, int probability)
    {
        int p = (0x7FFFFF - (probability << 15) + probability) >> 8;
        Trace?.WriteLine($"b {bit} {p}");
        EncodeBoolQ15(bit, (uint)p);
    }

    /// <summary>aom_write_bit.</summary>
    public void WriteBit(int bit) => Write(bit, 128);

    /// <summary>aom_write_literal (MSB first).</summary>
    public void WriteLiteral(int data, int bits)
    {
        for (int bit = bits - 1; bit >= 0; bit--) WriteBit(1 & (data >> bit));
    }

    private void TraceSym(char kind, int s, ReadOnlySpan<ushort> cdf, int nsymbs)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(kind).Append(' ').Append(s).Append(' ').Append(nsymbs);
        for (int i = 0; i < nsymbs - 1; i++) sb.Append(' ').Append(cdf[i]);
        Trace!.WriteLine(sb.ToString());
    }

    /// <summary>aom_stop_encode (od_ec_enc_done): the minimum number of bytes that decode the coded symbols whatever
    /// follows. Returns the tile's bytes.</summary>
    public byte[] Finish()
    {
        ulong m = 0x3FFF;
        ulong e = ((_low + m) & ~m) | (m + 1);
        int c = _cnt;
        int s = 10 + c;
        uint offs = _offs;
        int sBits = (s + 7) >> 3;
        int b = Math.Max(sBits, 0);
        if (offs + b > _buf.Length) Array.Resize(ref _buf, (int)(offs + b));
        if (s > 0)
        {
            ulong n = (1UL << (c + 16)) - 1;
            do
            {
                ushort val = (ushort)(e >> (c + 16));
                _buf[offs] = (byte)(val & 0xFF);
                if ((val & 0x100) != 0) PropagateCarryBwd(_buf, offs - 1);
                offs++;
                e &= n;
                s -= 8;
                c -= 8;
                n >>= 8;
            } while (s > 0);
        }
        return _buf.AsSpan(0, (int)offs).ToArray();
    }
}

/// <summary>struct aom_write_bit_buffer (aom_dsp/bitwriter_buffer.c): MSB-first raw bits for the OBU headers.</summary>
internal sealed class AomWriteBitBuffer
{
    private byte[] _buf = new byte[64];
    public int BitOffset;

    public void WriteBit(int bit)
    {
        int p = BitOffset >> 3, q = 7 - (BitOffset & 7);
        if (p >= _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
        if (q == 7) _buf[p] = (byte)(bit << q);
        else
        {
            _buf[p] &= (byte)~(1 << q);
            _buf[p] |= (byte)(bit << q);
        }
        BitOffset++;
    }

    public void WriteLiteral(int data, int bits)
    {
        for (int bit = bits - 1; bit >= 0; bit--) WriteBit((data >> bit) & 1);
    }

    public void WriteUnsignedLiteral(uint data, int bits)
    {
        for (int bit = bits - 1; bit >= 0; bit--) WriteBit((int)((data >> bit) & 1));
    }

    /// <summary>aom_wb_write_inv_signed_literal.</summary>
    public void WriteInvSignedLiteral(int data, int bits) => WriteLiteral(data, bits + 1);

    public bool IsByteAligned => (BitOffset & 7) == 0;

    /// <summary>aom_wb_bytes_written.</summary>
    public int BytesWritten => (BitOffset + 7) >> 3;

    public byte[] ToArray() => _buf.AsSpan(0, BytesWritten).ToArray();
}
