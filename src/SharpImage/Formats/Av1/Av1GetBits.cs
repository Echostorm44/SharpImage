// AV1 bitstream reader for OBU/header parsing
// Ported from dav1d: src/getbits.c, src/getbits.h
// Uses a 64-bit state buffer with byte-at-a-time refill, matching dav1d's design.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 bitstream reader for OBU header and frame header parsing.
/// Reads from a <see cref="ReadOnlySpan{T}"/> of bytes, MSB-first.
/// </summary>
/// <remarks>
/// This is the "raw bitstream" reader used for uncompressed OBU headers
/// (sequence header, frame header, etc). Entropy-coded tile data uses
/// <see cref="Av1Msac"/> (the multi-symbol arithmetic coder) instead.
///
/// Ported from dav1d getbits.c. The 64-bit state holds refilled bytes
/// left-aligned, with bits_left tracking how many valid bits remain.
/// </remarks>
public ref struct Av1GetBits
{
    private readonly ReadOnlySpan<byte> data;
    private int position;
    private ulong state;
    private int bitsLeft;
    private bool error;

    /// <summary>True if reading went past the end of data.</summary>
    public readonly bool Error => error;

    /// <summary>Current bit position relative to start of data.</summary>
    public readonly int BitsRead => position * 8 - bitsLeft;

    /// <summary>Current byte position (approximate, after refill).</summary>
    public readonly int BytePosition => position;

    public Av1GetBits(ReadOnlySpan<byte> data)
    {
        this.data = data;
        position = 0;
        state = 0;
        bitsLeft = 0;
        error = false;
    }

    /// <summary>Reads a single bit (0 or 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetBit()
    {
        if (bitsLeft == 0)
        {
            if (position >= data.Length)
            {
                error = true;
                return 0;
            }
            uint s = data[position++];
            bitsLeft = 7;
            state = (ulong)s << 57;
            return s >> 7;
        }

        ulong st = state;
        bitsLeft--;
        state = st << 1;
        return (uint)(st >> 63);
    }

    /// <summary>Reads a single bit as bool.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetBool() => GetBit() != 0;

    /// <summary>Reads n unsigned bits (1..32).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetBits(int n)
    {
        if ((uint)n > (uint)bitsLeft)
            Refill(n);
        ulong st = state;
        bitsLeft -= n;
        state = st << n;
        return (uint)(st >> (64 - n));
    }

    /// <summary>Reads n signed bits (1..32).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetSignedBits(int n)
    {
        if ((uint)n > (uint)bitsLeft)
            Refill(n);
        ulong st = state;
        bitsLeft -= n;
        state = st << n;
        return (int)((long)st >> (64 - n));
    }

    /// <summary>Reads a LEB128 (variable-length) unsigned integer.</summary>
    public uint GetUleb128()
    {
        ulong val = 0;
        int i = 0;
        uint more;

        do
        {
            uint v = GetBits(8);
            more = v & 0x80;
            val |= (ulong)(v & 0x7F) << i;
            i += 7;
        } while (more != 0 && i < 56);

        if (val > uint.MaxValue || more != 0)
        {
            error = true;
            return 0;
        }

        return (uint)val;
    }

    /// <summary>
    /// Reads a uniform-distributed value in range [0, max-1].
    /// max must be > 1.
    /// </summary>
    public uint GetUniform(uint max)
    {
        int l = 31 - BitOperations.LeadingZeroCount(max) + 1;
        uint m = (1u << l) - max;
        uint v = GetBits(l - 1);
        return v < m ? v : (v << 1) - m + GetBit();
    }

    /// <summary>Reads an unsigned variable-length code (exp-Golomb-like).</summary>
    public uint GetVlc()
    {
        if (GetBit() != 0)
            return 0;

        int nBits = 0;
        do
        {
            if (++nBits == 32)
                return uint.MaxValue;
        } while (GetBit() == 0);

        return ((1u << nBits) - 1) + GetBits(nBits);
    }

    /// <summary>
    /// Reads a subexponential coded signed value with reference.
    /// Used for global motion parameters and segmentation features.
    /// </summary>
    public int GetBitsSubexp(int reference, uint n)
    {
        return (int)GetBitsSubexpU((uint)(reference + (1 << (int)n)), 2u << (int)n) - (1 << (int)n);
    }

    /// <summary>Aligns to the next byte boundary by discarding remaining bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ByteAlign()
    {
        bitsLeft = 0;
        state = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refill(int n)
    {
        uint s = 0;
        do
        {
            if (position >= data.Length)
            {
                error = true;
                if (s != 0) break;
                return;
            }
            s = (s << 8) | data[position++];
            bitsLeft += 8;
        } while (n > bitsLeft);
        state |= (ulong)s << (64 - bitsLeft);
    }

    private uint GetBitsSubexpU(uint reference, uint n)
    {
        uint v = 0;

        for (int i = 0; ; i++)
        {
            int b = i != 0 ? 3 + i - 1 : 3;

            if (n < v + 3 * (1u << b))
            {
                v += GetUniform(n - v + 1);
                break;
            }

            if (GetBit() == 0)
            {
                v += GetBits(b);
                break;
            }

            v += 1u << b;
        }

        return reference * 2 <= n ? InvRecenter(reference, v) : n - InvRecenter(n - reference, v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint InvRecenter(uint r, uint v)
    {
        if (v > (r << 1))
            return v;
        else if ((v & 1) == 0)
            return (v >> 1) + r;
        else
            return r - ((v + 1) >> 1);
    }
}
