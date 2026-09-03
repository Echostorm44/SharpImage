// JPEG XL entropy decoding: ANS (asymmetric numeral systems) with alias tables, hybrid-uint
// tokenisation, LZ77, context maps, and the prefix-code alternative. Ported from libjxl
// (dec_ans.cc/.h, ans_common.cc, dec_context_map.cc) and validated bit-exactly against a
// Python prototype on libjxl-produced lossless files.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl;

internal static class JxlBits
{
    public const int AnsLogTabSize = 12;
    public const int AnsTabSize = 1 << AnsLogTabSize;
    public const int AnsSignature = 0x13;

    public static int CeilLog2(int x) => x <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(x - 1));

    public static int FloorLog2(int x) => 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)x);

    public static int UnpackSigned(uint u) => (int)(u >> 1) ^ -(int)(u & 1);

    public static uint ReadU32(JxlBitReader br, JxlBitReader.U32Enc e0, JxlBitReader.U32Enc e1, JxlBitReader.U32Enc e2, JxlBitReader.U32Enc e3)
        => br.ReadU32(e0, e1, e2, e3);

    public static int VarLenUint8(JxlBitReader br)
    {
        if (br.ReadBits(1) == 0)
        {
            return 0;
        }

        int nb = (int)br.ReadBits(3);
        return nb == 0 ? 1 : (int)br.ReadBits(nb) + (1 << nb);
    }
}

/// <summary>A hybrid-uint tokenisation config (split_exponent, msb_in_token, lsb_in_token).</summary>
internal readonly record struct HybridUintConfig(int SplitExp, int Msb, int Lsb);

/// <summary>One ANS alias-table entry (branchless symbol lookup).</summary>
internal struct AliasEntry
{
    public int Cutoff;
    public int Right;
    public int Freq0;
    public int Offsets1;
    public int Freq1XorFreq0;
}

/// <summary>Decoded entropy code: either ANS alias tables or prefix (Huffman) codes, plus config.</summary>
internal sealed class JxlAnsCode
{
    public bool Lz77Enabled;
    public uint Lz77MinSymbol;
    public uint Lz77MinLength;
    public HybridUintConfig Lz77LengthConfig;
    public bool UsePrefix;
    public int LogAlpha;
    public int NumHistograms;
    public byte[] ContextMap = [];
    public int DistanceContext;
    public HybridUintConfig[] UintConfigs = [];
    public AliasEntry[][]? AliasTables;
    public JxlHuffman[]? Huffs;
}

internal static class JxlEntropy
{
    private static readonly (int, int)[] Huff =
    [
        (3, 10), (7, 12), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (5, 0), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (6, 11), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (5, 0), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (7, 13), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (5, 0), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (6, 11), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
        (3, 10), (5, 0), (3, 7), (4, 3), (3, 6), (3, 8), (3, 9), (4, 5), (3, 10), (4, 4), (3, 7), (4, 1), (3, 6), (3, 8), (3, 9), (4, 2),
    ];

    private static int Gpcp(int logcount, int shift)
    {
        int r = Math.Min(logcount, shift - ((JxlBits.AnsLogTabSize - logcount) >> 1));
        return r < 0 ? 0 : r;
    }

    private static int[] CreateFlat(int alphabetSize, int total)
    {
        int baseVal = total / alphabetSize;
        int extra = total - (baseVal * alphabetSize);
        int[] c = new int[alphabetSize];
        for (int i = 0; i < alphabetSize; i++)
        {
            c[i] = i < extra ? baseVal + 1 : baseVal;
        }

        return c;
    }

    public static int[] ReadHistogram(JxlBitReader br, int precisionBits = JxlBits.AnsLogTabSize)
    {
        int range = 1 << precisionBits;
        if (br.ReadBits(1) == 1)
        {
            // simple
            int num = (int)br.ReadBits(1) + 1;
            int[] syms = new int[num];
            int mx = 0;
            for (int i = 0; i < num; i++)
            {
                syms[i] = JxlBits.VarLenUint8(br);
                mx = Math.Max(mx, syms[i]);
            }

            int[] counts = new int[mx + 1];
            if (num == 1)
            {
                counts[syms[0]] = range;
            }
            else
            {
                counts[syms[0]] = (int)br.ReadBits(precisionBits);
                counts[syms[1]] = range - counts[syms[0]];
            }

            return counts;
        }

        if (br.ReadBits(1) == 1)
        {
            // flat
            int alpha = JxlBits.VarLenUint8(br) + 1;
            return CreateFlat(alpha, range);
        }

        // full
        int ubl = JxlBits.FloorLog2(JxlBits.AnsLogTabSize + 1);
        int log = 0;
        while (log < ubl && br.ReadBits(1) == 1)
        {
            log++;
        }

        int shift = (int)((br.ReadBits(log) | (1u << log)) - 1);
        int length = JxlBits.VarLenUint8(br) + 3;
        int[] cnts = new int[length];
        int[] logcounts = new int[length];
        int[] same = new int[length];
        int omitLog = -1;
        int omitPos = -1;
        int idx2 = 0;
        while (idx2 < length)
        {
            int idx = (int)br.PeekBits(7);
            br.Consume(Huff[idx].Item1);
            logcounts[idx2] = Huff[idx].Item2 - 1;
            if (logcounts[idx2] == JxlBits.AnsLogTabSize)
            {
                int rle = JxlBits.VarLenUint8(br);
                same[idx2] = rle + 5;
                idx2 += rle + 3 + 1;
                continue;
            }

            if (logcounts[idx2] > omitLog)
            {
                omitLog = logcounts[idx2];
                omitPos = idx2;
            }

            idx2++;
        }

        int prev = 0;
        int numsame = 0;
        for (int i = 0; i < length; i++)
        {
            if (same[i] != 0)
            {
                numsame = same[i] - 1;
                prev = i > 0 ? cnts[i - 1] : 0;
            }

            if (numsame > 0)
            {
                cnts[i] = prev;
                numsame--;
            }
            else
            {
                int code = logcounts[i];
                if (i == omitPos || code < 0)
                {
                    continue;
                }

                if (shift == 0 || code == 0)
                {
                    cnts[i] = 1 << code;
                }
                else
                {
                    int bc = Gpcp(code, shift);
                    cnts[i] = (1 << code) + ((int)br.ReadBits(bc) << (code - bc));
                }
            }
        }

        int total = 0;
        foreach (int v in cnts)
        {
            total += v;
        }

        cnts[omitPos] = range - total;
        return cnts;
    }

    public static AliasEntry[] InitAliasTable(int[] distIn, int logAlpha)
    {
        var dist = new List<int>(distIn);
        while (dist.Count > 0 && dist[^1] == 0)
        {
            dist.RemoveAt(dist.Count - 1);
        }

        if (dist.Count == 0)
        {
            dist.Add(JxlBits.AnsTabSize);
        }

        int tableSize = 1 << logAlpha;
        int entrySize = JxlBits.AnsTabSize >> logAlpha;
        var a = new AliasEntry[tableSize];
        int single = -1;
        for (int s = 0; s < dist.Count; s++)
        {
            if (dist[s] == JxlBits.AnsTabSize)
            {
                single = s;
            }
        }

        if (single != -1)
        {
            for (int i = 0; i < tableSize; i++)
            {
                a[i] = new AliasEntry { Cutoff = 0, Right = single, Freq0 = 0, Offsets1 = entrySize * i, Freq1XorFreq0 = JxlBits.AnsTabSize };
            }

            return a;
        }

        int[] cutoffs = new int[tableSize];
        var under = new List<int>();
        var over = new List<int>();
        for (int i = 0; i < dist.Count; i++)
        {
            cutoffs[i] = dist[i];
            if (cutoffs[i] > entrySize)
            {
                over.Add(i);
            }
            else if (cutoffs[i] < entrySize)
            {
                under.Add(i);
            }
        }

        for (int i = dist.Count; i < tableSize; i++)
        {
            cutoffs[i] = 0;
            under.Add(i);
        }

        while (over.Count > 0)
        {
            int oi = over[^1];
            over.RemoveAt(over.Count - 1);
            int ui = under[^1];
            under.RemoveAt(under.Count - 1);
            int underby = entrySize - cutoffs[ui];
            cutoffs[oi] -= underby;
            a[ui].Right = oi;
            a[ui].Offsets1 = cutoffs[oi];
            if (cutoffs[oi] < entrySize)
            {
                under.Add(oi);
            }
            else if (cutoffs[oi] > entrySize)
            {
                over.Add(oi);
            }
        }

        for (int i = 0; i < tableSize; i++)
        {
            if (cutoffs[i] == entrySize)
            {
                a[i].Right = i;
                a[i].Offsets1 = 0;
                a[i].Cutoff = 0;
            }
            else
            {
                a[i].Offsets1 -= cutoffs[i];
                a[i].Cutoff = cutoffs[i];
            }

            int f0 = i < dist.Count ? dist[i] : 0;
            int i1 = a[i].Right;
            int f1 = i1 < dist.Count ? dist[i1] : 0;
            a[i].Freq0 = f0;
            a[i].Freq1XorFreq0 = f1 ^ f0;
        }

        return a;
    }

    // ─── ANS histogram + normalization (encoder side; inverse of ReadHistogram) ──────────────────────

    // The precision shift used for ANS histograms. Counts are quantized so they are representable at this
    // shift (each non-omit count's low bits beyond Gpcp(code, shift) are zero). Lower shift => coarser
    // counts but smaller headers. 12 = exact; a moderate value is what real JXL files use.
    public const int HistShift = 6;

    // Quantize a count to a value representable at `shift` (floors the mantissa to Gpcp(code) bits).
    private static int QuantizeCount(int f, int shift)
    {
        if (f <= 1)
        {
            return f;
        }

        int code = JxlBits.FloorLog2(f);
        int bc = Gpcp(code, shift);
        int drop = code - bc;
        return (1 << code) + (((f - (1 << code)) >> drop) << drop);
    }

    // Normalize raw symbol counts to frequencies summing to AnsTabSize (4096), each used symbol >= 1,
    // with every non-largest bin representable at `shift`. The largest bin (omit_pos) absorbs the
    // remainder, so it need not be representable — the decoder reconstructs it as range - sum(others).
    public static int[] NormalizeCounts(long[] raw, int shift)
    {
        int n = raw.Length;
        int[] freq = new int[n];
        long total = 0;
        foreach (long v in raw)
        {
            total += v;
        }

        if (total == 0)
        {
            return freq;
        }

        int used = 0, last = 0, big = 0;
        for (int i = 0; i < n; i++)
        {
            if (raw[i] > 0)
            {
                used++;
                last = i;
                if (raw[i] > raw[big])
                {
                    big = i;
                }
            }
        }

        if (used == 1)
        {
            freq[last] = JxlBits.AnsTabSize;
            return freq;
        }

        _ = big;

        // Proportional floor, each used symbol >= 1.
        long assigned = 0;
        for (int i = 0; i < n; i++)
        {
            if (raw[i] > 0)
            {
                int f = (int)Math.Floor((double)raw[i] / total * JxlBits.AnsTabSize);
                if (f < 1)
                {
                    f = 1;
                }

                freq[i] = f;
                assigned += f;
            }
        }

        // If we overshot AnsTabSize (from bumping tiny bins to 1), shave the largest bins back down.
        while (assigned > JxlBits.AnsTabSize)
        {
            int mx = -1;
            for (int i = 0; i < n; i++)
            {
                if (freq[i] > 1 && (mx < 0 || freq[i] > freq[mx]))
                {
                    mx = i;
                }
            }

            if (mx < 0)
            {
                break;
            }

            freq[mx]--;
            assigned--;
        }

        // Quantize every bin to a value representable at `shift`, so any of them can be written exactly.
        assigned = 0;
        for (int i = 0; i < n; i++)
        {
            if (freq[i] > 0)
            {
                freq[i] = Math.Max(1, QuantizeCount(freq[i], shift));
                assigned += freq[i];
            }
        }

        // The omit position (the one the decoder reconstructs as range - sum(others)) is the first bin
        // with the largest logcount. Give it the whole deficit so the total is exactly AnsTabSize; it need
        // not stay representable since its count bits are not written.
        int omit = -1, omitLc = -2;
        for (int i = 0; i < n; i++)
        {
            if (freq[i] > 0)
            {
                int lc = JxlBits.FloorLog2(freq[i]);
                if (lc > omitLc)
                {
                    omitLc = lc;
                    omit = i;
                }
            }
        }

        freq[omit] += (int)(JxlBits.AnsTabSize - assigned);
        return freq;
    }

    // Writes a distribution (sum == AnsTabSize; non-largest bins representable at `shift`) in the "full"
    // format. Inverse of ReadHistogram.
    public static void WriteHistogram(JxlBitWriter w, int[] counts, int shift)
    {
        int alphabet = counts.Length;
        int nz = 0, first = -1, second = -1;
        for (int i = 0; i < alphabet; i++)
        {
            if (counts[i] > 0)
            {
                nz++;
                if (first < 0)
                {
                    first = i;
                }
                else if (second < 0)
                {
                    second = i;
                }
            }
        }

        if (nz <= 2)
        {
            w.WriteBits(1, 1);                       // is_simple
            int num = Math.Max(1, nz);
            w.WriteBits((uint)(num - 1), 1);         // num - 1
            WriteVarLenUint8(w, first < 0 ? 0 : first);
            if (num == 2)
            {
                WriteVarLenUint8(w, second);
                w.WriteBits((uint)counts[first], JxlBits.AnsLogTabSize);
            }

            return;
        }

        int last = alphabet - 1;
        while (last > 0 && counts[last] == 0)
        {
            last--;
        }

        int length = Math.Max(3, last + 1);

        w.WriteBits(0, 1); // not simple
        w.WriteBits(0, 1); // not flat
        WriteShift(w, shift);
        WriteVarLenUint8(w, length - 3);

        int[] lc = new int[length];
        for (int i = 0; i < length; i++)
        {
            lc[i] = counts[i] <= 0 ? -1 : JxlBits.FloorLog2(counts[i]);
        }

        int omit = 0, omitVal = lc[0];
        for (int i = 1; i < length; i++)
        {
            if (lc[i] > omitVal)
            {
                omitVal = lc[i];
                omit = i;
            }
        }

        for (int i = 0; i < length; i++)
        {
            WriteLogCount(w, lc[i]);
        }

        for (int i = 0; i < length; i++)
        {
            if (i == omit || counts[i] <= 0)
            {
                continue;
            }

            int code = lc[i];
            if (code == 0)
            {
                continue;
            }

            int bc = Gpcp(code, shift);
            if (bc > 0)
            {
                int drop = code - bc;
                w.WriteBits((uint)((counts[i] - (1 << code)) >> drop), bc);
            }
        }
    }

    // Writes the shift as ReadHistogram expects: log (unary, capped at ubl=3) then log bits, so that
    // shift == (bits | (1 << log)) - 1.
    private static void WriteShift(JxlBitWriter w, int shift)
    {
        int v = shift + 1; // v in [1, 15]
        int log = JxlBits.FloorLog2(v);
        int bits = v - (1 << log);
        for (int i = 0; i < log; i++)
        {
            w.WriteBits(1, 1);
        }

        if (log < 3)
        {
            w.WriteBits(0, 1); // terminator (not present when capped at ubl == 3)
        }

        if (log > 0)
        {
            w.WriteBits((uint)bits, log);
        }
    }

    private static void WriteVarLenUint8(JxlBitWriter w, int n)
    {
        if (n == 0)
        {
            w.WriteBits(0, 1);
            return;
        }

        w.WriteBits(1, 1);
        if (n == 1)
        {
            w.WriteBits(0, 3);
            return;
        }

        int nb = JxlBits.FloorLog2(n);
        w.WriteBits((uint)nb, 3);
        w.WriteBits((uint)(n - (1 << nb)), nb);
    }

    // Inverse of the fixed logcount prefix code (the Huff table): for logcount value v (-1..12), emit its
    // canonical code. Built once from Huff by finding the first table entry decoding to v.
    private static (int Nbits, int Bits)[]? logCodeCache;

    private static void WriteLogCount(JxlBitWriter w, int v)
    {
        if (logCodeCache == null)
        {
            var map = new (int, int)[14]; // index = v + 1  (v in -1..12)
            var seen = new bool[14];
            for (int idx = 0; idx < Huff.Length; idx++)
            {
                (int nbits, int val) = Huff[idx];
                int lv = val - 1; // logcount
                int slot = lv + 1;
                if (slot >= 0 && slot < 14 && !seen[slot])
                {
                    seen[slot] = true;
                    map[slot] = (nbits, idx & ((1 << nbits) - 1));
                }
            }

            logCodeCache = map;
        }

        (int nb, int bits) = logCodeCache[v + 1];
        w.WriteBits((uint)bits, nb);
    }

    public static uint ReadHybridUintConfig(HybridUintConfig cfg, uint token, JxlBitReader br)
    {
        int splitToken = 1 << cfg.SplitExp;
        if (token < splitToken)
        {
            return token;
        }

        int nbits = (cfg.SplitExp - (cfg.Msb + cfg.Lsb) + (int)((token - splitToken) >> (cfg.Msb + cfg.Lsb))) & 31;
        uint low = token & (uint)((1 << cfg.Lsb) - 1);
        token >>= cfg.Lsb;
        uint bits = br.ReadBits(nbits);
        return ((((1u << cfg.Msb) | (token & (uint)((1 << cfg.Msb) - 1))) << nbits) | bits) << cfg.Lsb | low;
    }

    public static HybridUintConfig DecodeUintConfig(int logAlpha, JxlBitReader br)
    {
        int splitExp = (int)br.ReadBits(JxlBits.CeilLog2(logAlpha + 1));
        int msb = 0;
        int lsb = 0;
        if (splitExp != logAlpha)
        {
            msb = (int)br.ReadBits(JxlBits.CeilLog2(splitExp + 1));
            lsb = (int)br.ReadBits(JxlBits.CeilLog2(splitExp - msb + 1));
        }

        return new HybridUintConfig(splitExp, msb, lsb);
    }

    public static readonly int[,] SpecialDistances =
    {
        {0,1},{1,0},{1,1},{-1,1},{0,2},{2,0},{1,2},{-1,2},{2,1},{-2,1},{2,2},{-2,2},{0,3},{3,0},{1,3},{-1,3},{3,1},{-3,1},{2,3},{-2,3},{3,2},{-3,2},{0,4},{4,0},{1,4},{-1,4},{4,1},{-4,1},{3,3},{-3,3},{2,4},{-2,4},{4,2},{-4,2},{0,5},{3,4},{-3,4},{4,3},{-4,3},{5,0},{1,5},{-1,5},{5,1},{-5,1},{2,5},{-2,5},{5,2},{-5,2},{4,4},{-4,4},{3,5},{-3,5},{5,3},{-5,3},{0,6},{6,0},{1,6},{-1,6},{6,1},{-6,1},{2,6},{-2,6},{6,2},{-6,2},{4,5},{-4,5},{5,4},{-5,4},{3,6},{-3,6},{6,3},{-6,3},{0,7},{7,0},{1,7},{-1,7},{5,5},{-5,5},{7,1},{-7,1},{4,6},{-4,6},{6,4},{-6,4},{2,7},{-2,7},{7,2},{-7,2},{3,7},{-3,7},{7,3},{-7,3},{5,6},{-5,6},{6,5},{-6,5},{8,0},{4,7},{-4,7},{7,4},{-7,4},{8,1},{8,2},{6,6},{-6,6},{8,3},{5,7},{-5,7},{7,5},{-7,5},{8,4},{6,7},{-6,7},{7,6},{-7,6},{8,5},{7,7},{-7,7},{8,6},{8,7},
    };

    public const int NumSpecialDistances = 120;

    public static JxlAnsCode DecodeHistograms(int numContexts, JxlBitReader br, bool disallowLz77 = false)
    {
        var code = new JxlAnsCode
        {
            Lz77Enabled = br.ReadBool(),
        };
        if (code.Lz77Enabled)
        {
            code.Lz77MinSymbol = JxlBits.ReadU32(br, JxlBitReader.U32Enc.Val(224), JxlBitReader.U32Enc.Val(512), JxlBitReader.U32Enc.Val(4096), JxlBitReader.U32Enc.BitsOff(15, 8));
            code.Lz77MinLength = JxlBits.ReadU32(br, JxlBitReader.U32Enc.Val(3), JxlBitReader.U32Enc.Val(4), JxlBitReader.U32Enc.BitsOff(2, 5), JxlBitReader.U32Enc.BitsOff(8, 9));
            code.Lz77LengthConfig = DecodeUintConfig(8, br);
            numContexts++;
        }

        int numHistograms = 1;
        byte[] contextMap = new byte[numContexts];
        if (numContexts > 1)
        {
            contextMap = DecodeContextMap(numContexts, br, out numHistograms);
        }

        code.ContextMap = contextMap;
        code.DistanceContext = contextMap[^1];
        code.UsePrefix = br.ReadBool();
        code.LogAlpha = code.UsePrefix ? JxlHuffman.PrefixMaxBits : (int)br.ReadBits(2) + 5;
        code.NumHistograms = numHistograms;
        code.UintConfigs = new HybridUintConfig[numHistograms];
        for (int i = 0; i < numHistograms; i++)
        {
            code.UintConfigs[i] = DecodeUintConfig(code.LogAlpha, br);
        }

        if (code.UsePrefix)
        {
            // All cluster alphabet sizes are read first, then all histograms (libjxl/jxl-oxide order).
            code.Huffs = new JxlHuffman[numHistograms];
            int[] alphs = new int[numHistograms];
            for (int c = 0; c < numHistograms; c++)
            {
                alphs[c] = JxlHuffman.DecodeVarLenUint16(br) + 1;
            }

            for (int c = 0; c < numHistograms; c++)
            {
                code.Huffs[c] = alphs[c] > 1 ? new JxlHuffman(alphs[c], br) : JxlHuffman.TrivialCode();
            }
        }
        else
        {
            code.AliasTables = new AliasEntry[numHistograms][];
            for (int c = 0; c < numHistograms; c++)
            {
                int[] counts = ReadHistogram(br);
                code.AliasTables[c] = InitAliasTable(counts, code.LogAlpha);
            }
        }

        return code;
    }

    private static byte[] DecodeContextMap(int size, JxlBitReader br, out int numHtrees)
    {
        bool isSimple = br.ReadBool();
        byte[] cm = new byte[size];
        if (isSimple)
        {
            int bpe = (int)br.ReadBits(2);
            if (bpe > 0)
            {
                for (int i = 0; i < size; i++)
                {
                    cm[i] = (byte)br.ReadBits(bpe);
                }
            }
        }
        else
        {
            bool useMtf = br.ReadBool();
            JxlAnsCode code = DecodeHistograms(1, br, size <= 2);
            var rd = new JxlAnsReader(code, br);
            for (int i = 0; i < size; i++)
            {
                cm[i] = (byte)rd.ReadHybridUintCtx(0);
            }

            rd.CheckFinal();
            if (useMtf)
            {
                InverseMtf(cm);
            }
        }

        int mx = 0;
        foreach (byte b in cm)
        {
            mx = Math.Max(mx, b);
        }

        numHtrees = mx + 1;
        return cm;
    }

    /// <summary>read_clusters: decodes a distribution cluster map (used by HfBlockContext). Public wrapper.</summary>
    public static byte[] ReadClusters(int numDist, JxlBitReader br, out int numClusters)
    {
        if (numDist == 1)
        {
            numClusters = 1;
            return new byte[1];
        }

        return DecodeContextMap(numDist, br, out numClusters);
    }

    private static int AddLog2Ceil(uint x) => x == 0 ? 0 : (32 - System.Numerics.BitOperations.LeadingZeroCount(x + 1) - (System.Numerics.BitOperations.IsPow2(x + 1) ? 1 : 0));

    /// <summary>read_permutation: Lehmer-coded permutation of `size` elements skipping the first `skip`.</summary>
    public static int[] ReadPermutation(JxlBitReader br, JxlAnsReader rd, int size, int skip)
    {
        int GetContext(uint v) => Math.Min(7, AddLog2Ceil(v));
        int end = (int)rd.ReadHybridUintCtx(GetContext((uint)size));
        int[] lehmer = new int[end];
        uint prev = 0;
        for (int idx = 0; idx < end; idx++)
        {
            lehmer[idx] = (int)rd.ReadHybridUintCtx(GetContext(prev));
            prev = (uint)lehmer[idx];
        }

        var temp = new List<int>();
        for (int i = skip; i < size; i++)
        {
            temp.Add(i);
        }

        var perm = new List<int>(size);
        for (int i = 0; i < skip; i++)
        {
            perm.Add(i);
        }

        foreach (int l in lehmer)
        {
            perm.Add(temp[l]);
            temp.RemoveAt(l);
        }

        perm.AddRange(temp);
        return perm.ToArray();
    }

    private static void InverseMtf(byte[] v)
    {
        byte[] table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = (byte)i;
        }

        for (int i = 0; i < v.Length; i++)
        {
            int idx = v[i];
            byte val = table[idx];
            v[i] = val;
            for (int j = idx; j > 0; j--)
            {
                table[j] = table[j - 1];
            }

            table[0] = val;
        }
    }
}
