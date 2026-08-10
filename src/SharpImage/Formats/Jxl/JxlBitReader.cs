// JPEG XL codestream bit reader and field primitives (ISO/IEC 18181-1), ported from libjxl
// (lib/jxl/dec_bit_reader.h, fields.cc). JXL reads bits LSB-first within the byte stream.
//
// This is the foundation of a from-scratch pure-C# JPEG XL decoder. The container/codestream
// signature and headers are handled here; entropy decoding (ANS/prefix), Modular and VarDCT
// modes are layered on top. Until the decoder is complete it is not wired into JxlCoder.
using System;

namespace SharpImage.Formats.Jxl;

/// <summary>Reads bits (LSB-first) and JPEG XL field encodings from a codestream.</summary>
internal sealed class JxlBitReader
{
    private readonly byte[] data;
    private long bitPos;

    public JxlBitReader(byte[] data, int byteOffset = 0)
    {
        this.data = data;
        bitPos = (long)byteOffset * 8;
    }

    public long BitPosition => bitPos;

    public bool AtEnd => (bitPos >> 3) >= data.Length;

    /// <summary>Reads <paramref name="n"/> bits, least-significant bit first.</summary>
    public uint ReadBits(int n)
    {
        uint v = 0;
        for (int i = 0; i < n; i++)
        {
            int byteIdx = (int)(bitPos >> 3);
            int bit = byteIdx < data.Length ? (data[byteIdx] >> (int)(bitPos & 7)) & 1 : 0;
            v |= (uint)bit << i;
            bitPos++;
        }

        return v;
    }

    public bool ReadBool() => ReadBits(1) != 0;

    /// <summary>Reads <paramref name="n"/> bits without advancing the position.</summary>
    public uint PeekBits(int n)
    {
        long save = bitPos;
        uint v = ReadBits(n);
        bitPos = save;
        return v;
    }

    /// <summary>Advances the bit position by <paramref name="n"/> bits.</summary>
    public void Consume(int n) => bitPos += n;

    public void SeekBit(long bit) => bitPos = bit;

    /// <summary>Reads a value with the JXL Bits(n) + offset encoding.</summary>
    public uint ReadBitsOffset(int n, uint offset) => ReadBits(n) + offset;

    // A U32 field selects one of four encodings with a 2-bit selector. Each option is either
    // a constant (Val) or `bits` read bits plus an offset (BitsOffset).
    public readonly record struct U32Enc(bool IsVal, int Bits, uint Offset)
    {
        public static U32Enc Val(uint c) => new(true, 0, c);
        public static U32Enc BitsOff(int bits, uint offset) => new(false, bits, offset);
    }

    public uint ReadU32(U32Enc e0, U32Enc e1, U32Enc e2, U32Enc e3)
    {
        U32Enc e = (int)ReadBits(2) switch { 0 => e0, 1 => e1, 2 => e2, _ => e3 };
        return e.IsVal ? e.Offset : ReadBits(e.Bits) + e.Offset;
    }

    /// <summary>Reads a JXL U64 (fields.cc U64Coder::Read).</summary>
    public ulong ReadU64()
    {
        uint sel = ReadBits(2);
        if (sel == 0)
        {
            return 0;
        }

        if (sel == 1)
        {
            return 1 + ReadBits(4);
        }

        if (sel == 2)
        {
            return 17 + ReadBits(8);
        }

        ulong result = ReadBits(12);
        int shift = 12;
        while (ReadBits(1) != 0)
        {
            if (shift == 60)
            {
                result |= (ulong)ReadBits(4) << shift;
                break;
            }

            result |= (ulong)ReadBits(8) << shift;
            shift += 8;
        }

        return result;
    }

    /// <summary>Reads a JXL enum field (fields.h Enum): Val(0), Val(1), BitsOffset(4,2), BitsOffset(6,18).</summary>
    public uint ReadEnum() => ReadU32(U32Enc.Val(0), U32Enc.Val(1), U32Enc.BitsOff(4, 2), U32Enc.BitsOff(6, 18));

    /// <summary>Reads a 16-bit half-precision float.</summary>
    public float ReadF16()
    {
        uint h = ReadBits(16);
        int sign = (int)((h >> 15) & 1);
        int exp = (int)((h >> 10) & 0x1F);
        int man = (int)(h & 0x3FF);
        float val;
        if (exp == 0)
        {
            val = man / 1024f * MathF.Pow(2, -14);
        }
        else if (exp == 31)
        {
            val = man == 0 ? float.PositiveInfinity : float.NaN;
        }
        else
        {
            val = (1 + man / 1024f) * MathF.Pow(2, exp - 15);
        }

        return sign != 0 ? -val : val;
    }

    /// <summary>Advances to the next whole byte boundary (zero-fill padding).</summary>
    public void JumpToByteBoundary()
    {
        if ((bitPos & 7) != 0)
        {
            bitPos = (bitPos + 7) & ~7L;
        }
    }
}
