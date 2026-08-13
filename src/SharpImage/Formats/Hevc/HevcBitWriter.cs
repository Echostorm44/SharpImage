// MSB-first bit writer for HEVC RBSP output (mirror of BitstreamReader). Part of the pure-C#
// HEVC intra encoder that backs HEIC encoding.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Hevc;

/// <summary>Accumulates bits MSB-first into a byte buffer; used for RBSP payload construction.</summary>
internal sealed class HevcBitWriter
{
    private readonly List<byte> bytes = new();
    private uint cur;      // pending bits, left-aligned within the low `bitCount` bits
    private int bitCount;  // number of pending bits in `cur` (0..7)

    public int BitLength => (bytes.Count * 8) + bitCount;

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>, MSB first.</summary>
    public void PutBits(uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            PutBit((int)((value >> i) & 1));
        }
    }

    public void PutBit(int bit)
    {
        cur = (cur << 1) | (uint)(bit & 1);
        bitCount++;
        if (bitCount == 8)
        {
            bytes.Add((byte)cur);
            cur = 0;
            bitCount = 0;
        }
    }

    /// <summary>Appends a whole byte (only valid when byte-aligned).</summary>
    public void PutByte(uint b)
    {
        if (bitCount == 0)
        {
            bytes.Add((byte)b);
        }
        else
        {
            PutBits(b, 8);
        }
    }

    public bool IsByteAligned => bitCount == 0;

    /// <summary>rbsp_trailing_bits: a stop one-bit followed by zero bits to the next byte boundary.</summary>
    public void ByteAlignWithStopBit()
    {
        PutBit(1);
        while (bitCount != 0)
        {
            PutBit(0);
        }
    }

    public byte[] ToArray()
    {
        if (bitCount != 0)
        {
            // pad the final partial byte with zeros (callers align explicitly before finishing)
            byte[] outp = new byte[bytes.Count + 1];
            bytes.CopyTo(outp);
            outp[^1] = (byte)(cur << (8 - bitCount));
            return outp;
        }

        return bytes.ToArray();
    }
}
