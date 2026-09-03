// JPEG XL ANS (rANS) symbol *encoder*: the exact inverse of JxlAnsReader. Encodes a token stream with
// per-histogram normalized distributions (sum == AnsTabSize), emitting the 32-bit final state plus
// 16-bit renormalization words in the order the decoder consumes them. Extra (raw mantissa) bits are
// interleaved before each symbol in the reversed pass, exactly as libjxl's WriteTokens does. rANS is
// LIFO, so tokens are processed last-to-first; the initial encoder state is the ANS signature.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl;

/// <summary>One token to ANS-encode: which histogram, the symbol, and its raw extra bits.</summary>
internal readonly struct AnsToken
{
    public readonly int Histo;
    public readonly int Symbol;
    public readonly uint ExtraBits;
    public readonly int ExtraNBits;

    public AnsToken(int histo, int symbol, uint extraBits, int extraNBits)
    {
        Histo = histo;
        Symbol = symbol;
        ExtraBits = extraBits;
        ExtraNBits = extraNBits;
    }
}

internal sealed class JxlAnsWriter
{
    // Per histogram: freq[symbol] and encSlot[symbol][offset] -> table slot (built by inverting the
    // decoder's alias lookup, so the round-trip is exact regardless of alias-table geometry).
    private readonly int[][] freqs;
    private readonly int[][][] encSlot;
    public int LogAlpha { get; }

    public JxlAnsWriter(int[][] normalizedCounts, int logAlpha)
    {
        LogAlpha = logAlpha;
        int h = normalizedCounts.Length;
        freqs = new int[h][];
        encSlot = new int[h][][];
        for (int c = 0; c < h; c++)
        {
            BuildTables(normalizedCounts[c], logAlpha, out freqs[c], out encSlot[c]);
        }
    }

    // Invert the decoder: for every slot res in [0, AnsTabSize), compute (value, offset) exactly as
    // JxlAnsReader.ReadSymbol would, then record encSlot[value][offset] = res and freq[value].
    private static void BuildTables(int[] counts, int logAlpha, out int[] freq, out int[][] slot)
    {
        AliasEntry[] table = JxlEntropy.InitAliasTable(counts, logAlpha);
        int logEntrySize = JxlBits.AnsLogTabSize - logAlpha;
        int entrySizeMinus1 = (1 << logEntrySize) - 1;
        int alphabet = counts.Length;

        freq = new int[alphabet];
        var slots = new List<int>[alphabet];
        for (int res = 0; res < JxlBits.AnsTabSize; res++)
        {
            int i = res >> logEntrySize;
            int pos = res & entrySizeMinus1;
            AliasEntry e = table[i];
            bool greater = pos >= e.Cutoff;
            int f = e.Freq0 ^ (greater ? e.Freq1XorFreq0 : 0);
            int offset = (greater ? e.Offsets1 : 0) + pos;
            int value = greater ? e.Right : i;
            if (value >= alphabet)
            {
                continue; // slot belongs to a symbol beyond the trimmed alphabet
            }

            freq[value] = f;
            slots[value] ??= new List<int>();
            while (slots[value].Count <= offset)
            {
                slots[value].Add(0);
            }

            slots[value][offset] = res;
        }

        slot = new int[alphabet][];
        for (int s = 0; s < alphabet; s++)
        {
            slot[s] = slots[s]?.ToArray() ?? Array.Empty<int>();
        }
    }

    /// <summary>Encodes the tokens (in stream order) and writes state + interleaved bits to the writer.</summary>
    public void Encode(JxlBitWriter w, IReadOnlyList<AnsToken> tokens)
    {
        uint state = (uint)JxlBits.AnsSignature << 16;
        var bitsList = new List<uint>(tokens.Count * 2);
        var nbitsList = new List<int>(tokens.Count * 2);

        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            AnsToken t = tokens[i];

            // Extra bits first (this is the reversed pass).
            if (t.ExtraNBits > 0)
            {
                bitsList.Add(t.ExtraBits);
                nbitsList.Add(t.ExtraNBits);
            }

            int f = freqs[t.Histo][t.Symbol];
            uint ansBits = 0;
            int ansN = 0;
            if ((state >> (32 - JxlBits.AnsLogTabSize)) >= (uint)f)
            {
                ansBits = state & 0xFFFF;
                state >>= 16;
                ansN = 16;
            }

            int slot = encSlot[t.Histo][t.Symbol][state % (uint)f];
            state = ((state / (uint)f) << JxlBits.AnsLogTabSize) + (uint)slot;

            if (ansN > 0)
            {
                bitsList.Add(ansBits);
                nbitsList.Add(ansN);
            }
        }

        w.WriteBits(state, 32);
        for (int j = bitsList.Count - 1; j >= 0; j--)
        {
            w.WriteBits(bitsList[j], nbitsList[j]);
        }
    }
}
