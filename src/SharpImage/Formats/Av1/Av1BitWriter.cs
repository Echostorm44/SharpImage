// MSB-first bit writer for AV1 uncompressed headers (OBU header, sequence/frame headers, tile info). The
// inverse of Av1GetBits: f(n) writes the top bit first. The compressed tile symbol data uses Av1MsacWriter,
// not this. Used to assemble a decodable AV1 codestream for the AVIF encoder.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Av1;

internal sealed class Av1BitWriter
{
    private readonly List<byte> bytes = new(256);
    private int bitBuf;
    private int bitCount;

    /// <summary>Bit position (for size/alignment bookkeeping).</summary>
    public long BitPosition => ((long)bytes.Count * 8) + bitCount;

    public void PutBit(uint b)
    {
        bitBuf = (bitBuf << 1) | (int)(b & 1u);
        if (++bitCount == 8)
        {
            bytes.Add((byte)bitBuf);
            bitBuf = 0;
            bitCount = 0;
        }
    }

    public void PutBool(bool b) => PutBit(b ? 1u : 0u);

    /// <summary>Writes the low <paramref name="n"/> bits of <paramref name="value"/>, MSB first (f(n)).</summary>
    public void PutBits(uint value, int n)
    {
        for (int i = n - 1; i >= 0; i--)
        {
            PutBit((value >> i) & 1u);
        }
    }

    /// <summary>su(n): a two's-complement signed value in n bits (inverse of Av1GetBits.GetSignedBits). AV1's
    /// su(n) reads f(n) then subtracts 2^n when the top bit is set, so it is plain two's complement, not
    /// sign-magnitude. Value must fit in [-2^(n-1), 2^(n-1)-1].</summary>
    public void PutSignedBits(int value, int n)
    {
        PutBits((uint)value & ((n == 32 ? 0u : 1u << n) - 1u), n);
    }

    /// <summary>uvlc (GetVlc inverse): leadingZeros ones... actually AV1 uvlc = unary leadingZeros of 0s then a
    /// 1, then value bits. Matches Av1GetBits.GetVlc.</summary>
    public void PutUvlc(uint value)
    {
        // GetVlc: count leadingZeros while bit==0; then read that many extra bits. Encodes as:
        // value+1 in the exp-golomb-like form. leadingZeros = floor(log2(value+1)).
        uint v = value + 1;
        int leadingZeros = 0;
        while ((1u << (leadingZeros + 1)) <= v)
        {
            leadingZeros++;
        }

        for (int i = 0; i < leadingZeros; i++)
        {
            PutBit(0);
        }

        PutBit(1);
        PutBits(v - (1u << leadingZeros), leadingZeros);
    }

    /// <summary>ns(n) uniform code (GetUniform inverse): values below the split use l-1 bits, the rest l bits.</summary>
    public void PutUniform(uint value, uint max)
    {
        if (max <= 1)
        {
            return;
        }

        int l = 0;
        while ((1u << l) < max)
        {
            l++;
        }

        uint m = (uint)((1 << l) - (int)max);
        if (value < m)
        {
            PutBits(value, l - 1);
        }
        else
        {
            uint coded = value + m;
            PutBits(coded >> 1, l - 1);
            PutBit(coded & 1u);
        }
    }

    /// <summary>Byte-aligned unsigned LEB128 (for OBU sizes). Must be called on a byte boundary.</summary>
    public void PutUleb128(uint value)
    {
        do
        {
            uint b = value & 0x7Fu;
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80u;
            }

            PutBits(b, 8);
        }
        while (value != 0);
    }

    /// <summary>Pads with zero bits to the next byte boundary.</summary>
    public void ByteAlign()
    {
        while (bitCount != 0)
        {
            PutBit(0);
        }
    }

    /// <summary>trailing_bits(): a 1 bit then zero-pad to a byte boundary (ends frame_header_obu / tile_group).</summary>
    public void TrailingBits()
    {
        PutBit(1);
        ByteAlign();
    }

    public byte[] ToArray()
    {
        ByteAlign();
        return bytes.ToArray();
    }

    public int ByteLength
    {
        get
        {
            ByteAlign();
            return bytes.Count;
        }
    }
}
