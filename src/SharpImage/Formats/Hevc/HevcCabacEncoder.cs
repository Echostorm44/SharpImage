// CABAC binary arithmetic ENCODER for HEVC — the exact mirror of HevcCabacDecoder, sharing
// HevcCabacTables (LPS ranges, state transitions, context init). Arithmetic engine ported from
// kvazaar cabac.c. Part of the pure-C# HEVC intra encoder backing HEIC encoding.
using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Hevc;

/// <summary>
/// Encodes syntax bins into a CABAC bitstream. Context states use the same
/// <c>(stateIdx &lt;&lt; 1) | valMps</c> layout as the decoder.
/// </summary>
internal sealed class HevcCabacEncoder
{
    private readonly byte[] contextStates;
    private readonly HevcBitWriter writer;

    private uint low;
    private uint range;
    private int bitsLeft;
    private int numBufferedBytes;
    private uint bufferedByte;

    public HevcCabacEncoder(HevcBitWriter output)
    {
        writer = output;
        contextStates = new byte[HevcCabacContextIndex.TotalContexts];
    }

    /// <summary>Initializes all context models from slice QP + init type (identical to the decoder).</summary>
    public void InitializeContexts(int sliceQp, int initType)
    {
        ReadOnlySpan<byte> initValues = HevcCabacTables.GetInitValues(initType);
        int count = Math.Min(HevcCabacContextIndex.TotalContexts, initValues.Length);
        for (int i = 0; i < count; i++)
        {
            contextStates[i] = HevcCabacTables.ComputeInitState(initValues[i], sliceQp);
        }

        for (int i = count; i < contextStates.Length; i++)
        {
            contextStates[i] = 0;
        }
    }

    /// <summary>Resets the arithmetic engine (9.3.2.5 / kvz_cabac_start).</summary>
    public void Start()
    {
        low = 0;
        range = 510;
        bitsLeft = 23;
        numBufferedBytes = 0;
        bufferedByte = 0xff;
    }

    /// <summary>Encodes one context-coded bin.</summary>
    public void EncodeBin(int contextIndex, int binValue)
    {
        byte state = contextStates[contextIndex];
        int stateIdx = state >> 1;
        int valMps = state & 1;

        int rangeIndex = (int)(range >> 6) & 3;
        uint lps = HevcCabacTables.GetLpsRange(stateIdx, rangeIndex);
        range -= lps;

        if ((binValue != 0 ? 1 : 0) != valMps)
        {
            // LPS
            int numBits = RenormCount(lps);
            low = (low + range) << numBits;
            range = lps << numBits;

            if (stateIdx == 0)
            {
                valMps = 1 - valMps;
            }

            stateIdx = HevcCabacTables.TransitionIndexLps[stateIdx];
            bitsLeft -= numBits;
        }
        else
        {
            stateIdx = HevcCabacTables.TransitionIndexMps[stateIdx];
            contextStates[contextIndex] = (byte)((stateIdx << 1) | valMps);
            if (range >= 256)
            {
                return;
            }

            low <<= 1;
            range <<= 1;
            bitsLeft--;
            if (bitsLeft < 12)
            {
                WriteOut();
            }

            return;
        }

        contextStates[contextIndex] = (byte)((stateIdx << 1) | valMps);
        if (bitsLeft < 12)
        {
            WriteOut();
        }
    }

    /// <summary>Encodes a single bypass (equiprobable) bin.</summary>
    public void EncodeBypass(int binValue)
    {
        low <<= 1;
        if (binValue != 0)
        {
            low += range;
        }

        bitsLeft--;
        if (bitsLeft < 12)
        {
            WriteOut();
        }
    }

    /// <summary>Encodes <paramref name="numBins"/> bypass bins from the low bits of <paramref name="binValues"/> (MSB first).</summary>
    public void EncodeBypassBins(uint binValues, int numBins)
    {
        while (numBins > 8)
        {
            numBins -= 8;
            uint pattern = binValues >> numBins;
            low <<= 8;
            low += range * pattern;
            binValues -= pattern << numBins;
            bitsLeft -= 8;
            if (bitsLeft < 12)
            {
                WriteOut();
            }
        }

        low <<= numBins;
        low += range * binValues;
        bitsLeft -= numBins;
        if (bitsLeft < 12)
        {
            WriteOut();
        }
    }

    /// <summary>Encodes a terminating bin (end_of_slice_segment_flag / pcm_flag).</summary>
    public void EncodeTerminate(int binValue)
    {
        range -= 2;
        if (binValue != 0)
        {
            low += range;
            low <<= 7;
            range = 2 << 7;
            bitsLeft -= 7;
        }
        else if (range >= 256)
        {
            return;
        }
        else
        {
            low <<= 1;
            range <<= 1;
            bitsLeft--;
        }

        if (bitsLeft < 12)
        {
            WriteOut();
        }
    }

    /// <summary>Flushes the arithmetic engine after a terminate bin (kvz_cabac_finish); leaves the writer byte-aligned.</summary>
    public void Finish()
    {
        if ((low >> (32 - bitsLeft)) != 0)
        {
            writer.PutByte(bufferedByte + 1);
            while (numBufferedBytes > 1)
            {
                writer.PutByte(0);
                numBufferedBytes--;
            }

            low -= 1u << (32 - bitsLeft);
        }
        else
        {
            if (numBufferedBytes > 0)
            {
                writer.PutByte(bufferedByte);
            }

            while (numBufferedBytes > 1)
            {
                writer.PutByte(0xff);
                numBufferedBytes--;
            }
        }

        int bits = 24 - bitsLeft;
        writer.PutBits(low >> 8, bits);
    }

    private void WriteOut()
    {
        uint leadByte = low >> (24 - bitsLeft);
        bitsLeft += 8;
        low &= 0xffffffffu >> bitsLeft;

        if (leadByte == 0xff)
        {
            numBufferedBytes++;
        }
        else
        {
            if (numBufferedBytes > 0)
            {
                uint carry = leadByte >> 8;
                uint b = bufferedByte + carry;
                bufferedByte = leadByte & 0xff;
                writer.PutByte(b & 0xff);

                b = (0xff + carry) & 0xff;
                while (numBufferedBytes > 1)
                {
                    writer.PutByte(b);
                    numBufferedBytes--;
                }
            }
            else
            {
                numBufferedBytes = 1;
                bufferedByte = leadByte;
            }
        }
    }

    /// <summary>Left-shifts needed to renormalize an LPS sub-range back into [256, 512).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RenormCount(uint lps)
    {
        int n = 0;
        while (lps < 256)
        {
            lps <<= 1;
            n++;
        }

        return n;
    }
}
