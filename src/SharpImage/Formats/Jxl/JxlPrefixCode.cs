// JPEG XL prefix (Huffman) code *encoder*: builds a length-limited canonical prefix code from
// symbol frequencies and writes it in exactly the form JxlHuffman reads back (dec_huffman.cc /
// huffman_table.cc). Canonical codes are assigned with the same GetNextKey recurrence the decoder's
// BuildTable uses, so the reversed-canonical code an encoder emits is the value the reader's
// LSB-first table lookup returns.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl;

/// <summary>A built prefix code ready to serialise and to encode symbols with.</summary>
internal sealed class JxlPrefixCode
{
    private const int PrefixMaxBits = 15;
    private const int CodeLengthCodes = 18;
    private static readonly int[] CodeLengthCodeOrder =
        [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    public int AlphabetSize { get; }
    public bool Trivial => AlphabetSize <= 1;   // decoder uses JxlHuffman.TrivialCode(): no header, 0 bits/symbol
    private readonly int[] lengths;             // per-symbol code length (0 = unused)
    private readonly uint[] codes;              // per-symbol reversed-canonical code
    private readonly int singleSymbol;          // >=0 when exactly one symbol is used (simple code, num=1)

    public JxlPrefixCode(long[] freq, int alphabetSize)
    {
        AlphabetSize = alphabetSize;
        lengths = new int[Math.Max(1, alphabetSize)];
        codes = new uint[Math.Max(1, alphabetSize)];
        if (alphabetSize <= 1)
        {
            singleSymbol = -1;
            return;
        }

        var used = new List<int>();
        for (int i = 0; i < alphabetSize; i++)
        {
            if (freq[i] > 0)
            {
                used.Add(i);
            }
        }

        if (used.Count == 1)
        {
            singleSymbol = used[0];
            lengths[singleSymbol] = 0; // simple code num=1: the symbol consumes 0 bits
            return;
        }

        singleSymbol = -1;
        BuildLengths(freq, used, PrefixMaxBits, lengths);
        AssignCanonicalCodes(lengths, codes);
    }

    /// <summary>Emits one symbol's code (nothing for a trivial or single-symbol code).</summary>
    public void WriteSymbol(JxlBitWriter w, int symbol)
    {
        if (Trivial || singleSymbol >= 0)
        {
            return;
        }

        w.WriteBits(codes[symbol], lengths[symbol]);
    }

    /// <summary>Serialises the prefix-code header (JxlHuffman ctor input). No output for a trivial code.</summary>
    public void WriteHeader(JxlBitWriter w)
    {
        if (Trivial)
        {
            return; // decoder builds JxlHuffman.TrivialCode() without reading anything
        }

        if (singleSymbol >= 0)
        {
            // Simple prefix code, num symbols = 1.
            w.WriteBits(1, 2); // simple-or-skip == 1 => simple code
            int maxBits = AlphabetSize > 1 ? FloorLog2((uint)(AlphabetSize - 1)) + 1 : 0;
            w.WriteBits(0, 2); // num-1 == 0 => 1 symbol
            w.WriteBits((uint)singleSymbol, maxBits);
            return;
        }

        WriteComplexHeader(w);
    }

    private void WriteComplexHeader(JxlBitWriter w)
    {
        w.WriteBits(0, 2); // simple-or-skip == 0 => complex code, start at code-length-code index 0

        int last = AlphabetSize - 1;
        while (last > 0 && lengths[last] == 0)
        {
            last--;
        }

        // Frequencies of each code-length value (the code-length-code alphabet, 0..15) over the
        // symbol-length sequence the decoder will read (indices 0..last). Values 16/17 (repeat) are
        // unused: every length is emitted literally.
        long[] clcFreq = new long[CodeLengthCodes];
        for (int i = 0; i <= last; i++)
        {
            clcFreq[lengths[i]]++;
        }

        int distinct = 0;
        foreach (long f in clcFreq)
        {
            if (f > 0)
            {
                distinct++;
            }
        }

        // The code-length code itself must be a complete prefix code, which needs >= 2 symbols. If
        // every symbol happens to share one length, add a phantom (never-emitted) length-0 codeword.
        if (distinct == 1 && clcFreq[0] == 0)
        {
            clcFreq[0] = 1;
        }

        int[] clcLen = new int[CodeLengthCodes];
        BuildLengths(clcFreq, UsedIndices(clcFreq), 5, clcLen); // clc code lengths are read via a fixed 5-bit code
        uint[] clcCodes = new uint[CodeLengthCodes];
        AssignCanonicalCodes(clcLen, clcCodes);

        // Emit the clc code lengths in CodeLengthCodeOrder, up to the last non-zero one (the decoder
        // stops once the clc code's Kraft budget is exhausted).
        int lastOrder = 0;
        for (int i = 0; i < CodeLengthCodes; i++)
        {
            if (clcLen[CodeLengthCodeOrder[i]] > 0)
            {
                lastOrder = i;
            }
        }

        for (int i = 0; i <= lastOrder; i++)
        {
            WriteClcLength(w, clcLen[CodeLengthCodeOrder[i]]);
        }

        // Emit each symbol's code length (indices 0..last) using the clc code.
        for (int i = 0; i <= last; i++)
        {
            int L = lengths[i];
            w.WriteBits(clcCodes[L], clcLen[L]);
        }
    }

    // The fixed code (SimpleClcHuff) the decoder uses to read clc lengths, inverted: value -> (bits, count).
    private static void WriteClcLength(JxlBitWriter w, int v)
    {
        switch (v)
        {
            case 0: w.WriteBits(0, 2); break;
            case 1: w.WriteBits(7, 4); break;
            case 2: w.WriteBits(3, 3); break;
            case 3: w.WriteBits(2, 2); break;
            case 4: w.WriteBits(1, 2); break;
            case 5: w.WriteBits(15, 4); break;
            default: throw new InvalidOperationException($"code-length-code length {v} exceeds 5.");
        }
    }

    private static List<int> UsedIndices(long[] freq)
    {
        var used = new List<int>();
        for (int i = 0; i < freq.Length; i++)
        {
            if (freq[i] > 0)
            {
                used.Add(i);
            }
        }

        return used;
    }

    // Canonical code assignment mirroring JxlHuffman.BuildTable / GetNextKey: process ascending
    // length, then ascending symbol, advancing a reversed-canonical key.
    private static void AssignCanonicalCodes(int[] lengths, uint[] codes)
    {
        int key = 0;
        for (int len = 1; len <= PrefixMaxBits; len++)
        {
            for (int sym = 0; sym < lengths.Length; sym++)
            {
                if (lengths[sym] == len)
                {
                    codes[sym] = (uint)key;
                    key = GetNextKey(key, len);
                }
            }
        }
    }

    private static int GetNextKey(int key, int len)
    {
        int step = 1 << (len - 1);
        while ((key & step) != 0)
        {
            step >>= 1;
        }

        return (key & (step - 1)) + step;
    }

    // Optimal length-limited canonical lengths: ordinary Huffman lengths, then the zlib overflow
    // repair to cap at `limit`, then re-assign the length multiset shortest-first to the most
    // frequent symbols (optimal for that multiset).
    private static void BuildLengths(long[] freq, List<int> used, int limit, int[] outLengths)
    {
        int m = used.Count;
        if (m == 0)
        {
            return;
        }

        if (m == 1)
        {
            outLengths[used[0]] = 1;
            return;
        }

        // Ordinary Huffman via a min-heap over node weights.
        int maxNodes = (2 * m) - 1;
        long[] wt = new long[maxNodes];
        int[] par = new int[maxNodes];
        for (int i = 0; i < maxNodes; i++)
        {
            par[i] = -1;
        }

        for (int i = 0; i < m; i++)
        {
            wt[i] = freq[used[i]];
        }

        // Simple binary heap of node indices keyed by (weight, index) for determinism.
        var heap = new List<int>();
        for (int i = 0; i < m; i++)
        {
            HeapPush(heap, wt, i);
        }

        int next = m;
        while (heap.Count > 1)
        {
            int a = HeapPop(heap, wt);
            int b = HeapPop(heap, wt);
            wt[next] = wt[a] + wt[b];
            par[a] = next;
            par[b] = next;
            HeapPush(heap, wt, next);
            next++;
        }

        // Depth of each leaf = Huffman code length.
        int[] leafLen = new int[m];
        int maxLen = 0;
        for (int i = 0; i < m; i++)
        {
            int d = 0;
            int k = i;
            while (par[k] != -1)
            {
                d++;
                k = par[k];
            }

            leafLen[i] = d;
            maxLen = Math.Max(maxLen, d);
        }

        // bl_count[len] = number of leaves at that length.
        int hi = Math.Max(maxLen, limit);
        int[] blCount = new int[hi + 2];
        for (int i = 0; i < m; i++)
        {
            blCount[leafLen[i]]++;
        }

        if (maxLen > limit)
        {
            for (int bits = limit + 1; bits <= maxLen; bits++)
            {
                blCount[limit] += blCount[bits];
                blCount[bits] = 0;
            }

            long total = 0;
            for (int b = 1; b <= limit; b++)
            {
                total += (long)blCount[b] << (limit - b);
            }

            long goal = 1L << limit;
            while (total > goal)
            {
                blCount[limit]--;
                int b = limit - 1;
                while (blCount[b] == 0)
                {
                    b--;
                }

                blCount[b]--;
                blCount[b + 1] += 2;
                total--;
            }
        }

        // Assign the length multiset shortest-first to the most frequent symbols.
        int[] orderByFreq = new int[m];
        for (int i = 0; i < m; i++)
        {
            orderByFreq[i] = i;
        }

        Array.Sort(orderByFreq, (x, y) =>
        {
            int c = wt[y].CompareTo(wt[x]); // descending frequency
            return c != 0 ? c : used[x].CompareTo(used[y]);
        });

        int idx = 0;
        for (int len = 1; len <= limit; len++)
        {
            for (int c = 0; c < blCount[len]; c++)
            {
                outLengths[used[orderByFreq[idx++]]] = len;
            }
        }
    }

    private static void HeapPush(List<int> heap, long[] wt, int node)
    {
        heap.Add(node);
        int i = heap.Count - 1;
        while (i > 0)
        {
            int p = (i - 1) / 2;
            if (Less(wt, heap[i], heap[p]))
            {
                (heap[i], heap[p]) = (heap[p], heap[i]);
                i = p;
            }
            else
            {
                break;
            }
        }
    }

    private static int HeapPop(List<int> heap, long[] wt)
    {
        int top = heap[0];
        int last = heap.Count - 1;
        heap[0] = heap[last];
        heap.RemoveAt(last);
        int i = 0;
        int n = heap.Count;
        while (true)
        {
            int l = (2 * i) + 1;
            int r = l + 1;
            int sm = i;
            if (l < n && Less(wt, heap[l], heap[sm]))
            {
                sm = l;
            }

            if (r < n && Less(wt, heap[r], heap[sm]))
            {
                sm = r;
            }

            if (sm == i)
            {
                break;
            }

            (heap[i], heap[sm]) = (heap[sm], heap[i]);
            i = sm;
        }

        return top;
    }

    // Weight then node-index tie-break, for deterministic trees.
    private static bool Less(long[] wt, int a, int b) => wt[a] != wt[b] ? wt[a] < wt[b] : a < b;

    private static int FloorLog2(uint x) => 31 - System.Numerics.BitOperations.LeadingZeroCount(x);
}
