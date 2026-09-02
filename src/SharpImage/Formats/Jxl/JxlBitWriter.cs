// JPEG XL codestream bit writer: the exact mirror of JxlBitReader (ISO/IEC 18181-1). Bits are
// packed least-significant-bit-first within the byte stream, and the field encoders below emit the
// same U32 / U64 / Enum / VarLenUint encodings the reader consumes. This is the foundation of the
// from-scratch pure-C# JPEG XL *encoder* (see JxlEncoder).
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl;

/// <summary>Writes bits (LSB-first) and JPEG XL field encodings into a codestream buffer.</summary>
internal sealed class JxlBitWriter
{
    private readonly List<byte> bytes = new();
    private int curByte;
    private int curBits; // number of bits already filled in curByte (0..7)

    /// <summary>Number of whole bits written so far.</summary>
    public long BitPosition => ((long)bytes.Count * 8) + curBits;

    /// <summary>Writes <paramref name="n"/> bits of <paramref name="v"/>, least-significant bit first.</summary>
    public void WriteBits(uint v, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int bit = (int)((v >> i) & 1);
            curByte |= bit << curBits;
            curBits++;
            if (curBits == 8)
            {
                bytes.Add((byte)curByte);
                curByte = 0;
                curBits = 0;
            }
        }
    }

    public void WriteBool(bool b) => WriteBits(b ? 1u : 0u, 1);

    /// <summary>Pads the current partial byte with zero bits, reaching a whole-byte boundary.</summary>
    public void JumpToByteBoundary()
    {
        if (curBits != 0)
        {
            bytes.Add((byte)curByte);
            curByte = 0;
            curBits = 0;
        }
    }

    /// <summary>Finalises the stream (zero-padding any partial final byte) and returns the bytes.</summary>
    public byte[] ToArray()
    {
        JumpToByteBoundary();
        return bytes.ToArray();
    }

    /// <summary>Appends already-byte-aligned bytes; the writer must be on a byte boundary.</summary>
    public void AppendBytes(ReadOnlySpan<byte> data)
    {
        if (curBits != 0)
        {
            throw new InvalidOperationException("AppendBytes requires a byte-aligned writer.");
        }

        foreach (byte b in data)
        {
            bytes.Add(b);
        }
    }

    // A U32 field: pick the first of four encodings that can represent the value, then emit a 2-bit
    // selector plus (for a BitsOffset option) the bits. Mirrors JxlBitReader.ReadU32.
    public void WriteU32(uint val, JxlBitReader.U32Enc e0, JxlBitReader.U32Enc e1, JxlBitReader.U32Enc e2, JxlBitReader.U32Enc e3)
    {
        JxlBitReader.U32Enc[] opts = { e0, e1, e2, e3 };
        for (int s = 0; s < 4; s++)
        {
            JxlBitReader.U32Enc e = opts[s];
            if (e.IsVal)
            {
                if (val == e.Offset)
                {
                    WriteBits((uint)s, 2);
                    return;
                }
            }
            else if (val >= e.Offset && (e.Bits >= 32 || (val - e.Offset) < (1u << e.Bits)))
            {
                WriteBits((uint)s, 2);
                WriteBits(val - e.Offset, e.Bits);
                return;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(val), $"No U32 encoding option fits value {val}.");
    }

    /// <summary>Writes a JXL U64 (fields.cc U64Coder::Write). Mirrors JxlBitReader.ReadU64.</summary>
    public void WriteU64(ulong v)
    {
        if (v == 0)
        {
            WriteBits(0, 2);
            return;
        }

        if (v <= 16)
        {
            WriteBits(1, 2);
            WriteBits((uint)(v - 1), 4);
            return;
        }

        if (v <= 16 + 256)
        {
            WriteBits(2, 2);
            WriteBits((uint)(v - 17), 8);
            return;
        }

        WriteBits(3, 2);
        WriteBits((uint)(v & 0xFFF), 12);
        v >>= 12;
        int shift = 12;
        while (v > 0)
        {
            WriteBits(1, 1); // continuation
            if (shift == 60)
            {
                WriteBits((uint)(v & 0xF), 4);
                return; // reader breaks after the 4-bit final group
            }

            WriteBits((uint)(v & 0xFF), 8);
            v >>= 8;
            shift += 8;
        }

        WriteBits(0, 1); // terminating (no more continuation)
    }

    /// <summary>Writes a JXL enum field. Mirrors JxlBitReader.ReadEnum.</summary>
    public void WriteEnum(uint v) => WriteU32(v, JxlBitReader.U32Enc.Val(0), JxlBitReader.U32Enc.Val(1), JxlBitReader.U32Enc.BitsOff(4, 2), JxlBitReader.U32Enc.BitsOff(6, 18));

    /// <summary>Writes a VarLenUint16 (mirror of JxlHuffman.DecodeVarLenUint16).</summary>
    public void WriteVarLenUint16(int value)
    {
        if (value == 0)
        {
            WriteBits(0, 1);
            return;
        }

        WriteBits(1, 1);
        if (value == 1)
        {
            WriteBits(0, 4); // nbits==0 => value 1
            return;
        }

        int nbits = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value); // floor(log2)
        WriteBits((uint)nbits, 4);
        WriteBits((uint)(value - (1 << nbits)), nbits);
    }
}
