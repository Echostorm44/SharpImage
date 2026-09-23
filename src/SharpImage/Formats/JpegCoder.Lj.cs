using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

// A port of libjpeg-turbo 3.1's default decompression, pixel-exact: baseline / extended / progressive DCT with Huffman
// or arithmetic entropy coding (SOF0/1/2/9/10) at 8 or 12 bits, lossless (SOF3, precision 2..16, predictors 1..7,
// point transform), the accurate integer IDCT (jidctint.c islow) with its range-limit table, jdsample.c's upsampler
// choice (fancy h2v1 / h1v2 / h2v2, else replication), jdcolor.c's fixed-point YCbCr -> RGB and YCCK -> CMYK, and
// jdapimin.c's colour-space guess (JFIF / Adobe APP14 / component ids). Output: grey and RGB as sRGB frames, CMYK /
// YCCK as CMYK frames (Adobe-inverted samples flipped to ink amounts), samples scaled from the file's range to 16 bits.
// Everything libjpeg-turbo rejects (hierarchical, arithmetic lossless, lossless colour conversion, fractional
// sampling) returns null.
public static partial class JpegCoder
{
    private sealed class LjComp
    {
        public int Id, H, V, Tq, DcTbl, AcTbl;
        public int WidthInBlocks, HeightInBlocks, AllocW, AllocH, DownW, DownH;
        public short[] Coef = [];      // DCT: AllocW * AllocH blocks of 64 (natural order)
        public int[] Diff = [];        // lossless: AllocW * AllocH differences, then samples
        public int[] Samples = [];     // lossless output samples (WidthInBlocks * HeightInBlocks)
        public int LastDc, DcContext;
    }

    private sealed class LjHuff
    {
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValOffset = new int[18];
        public readonly byte[] Vals = new byte[256];
        public readonly int[] Look = new int[256];   // (length << 8) | value, 0 = not in the lookahead

        public LjHuff(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> vals)
        {
            vals.CopyTo(Vals);
            Span<int> code = stackalloc int[257];
            Span<byte> size = stackalloc byte[257];
            int p = 0;
            for (int l = 1; l <= 16; l++)
                for (int i = 0; i < counts[l - 1]; i++) size[p++] = (byte)l;
            size[p] = 0;
            int c = 0, si = size[0];
            p = 0;
            while (size[p] != 0)
            {
                while (size[p] == si) code[p++] = c++;
                c <<= 1;
                si++;
            }
            p = 0;
            for (int l = 1; l <= 16; l++)
            {
                if (counts[l - 1] != 0)
                {
                    ValOffset[l] = p - code[p];
                    p += counts[l - 1];
                    MaxCode[l] = code[p - 1];
                }
                else MaxCode[l] = -1;
            }
            MaxCode[17] = 0xFFFFF;
            p = 0;
            for (int l = 1; l <= 8; l++)
                for (int i = 0; i < counts[l - 1]; i++, p++)
                {
                    int lookbits = code[p] << (8 - l);
                    for (int ctr = 1 << (8 - l); ctr > 0; ctr--) Look[lookbits++] = (l << 8) | vals[p];
                }
        }
    }

    // The entropy-coded segment reader shared by the Huffman and arithmetic decoders: stuffed zero bytes removed, a
    // marker ends the data (zeros are supplied from then on, as libjpeg does).
    private sealed class LjBits
    {
        private readonly byte[] d;
        public int Pos;
        public int UnreadMarker;
        private ulong buf;
        private int bits;

        public LjBits(byte[] data, int pos) { d = data; Pos = pos; }

        // Next data byte (0xFF00 -> 0xFF), or -1 at a marker (left unread).
        public int NextByte()
        {
            if (UnreadMarker != 0 || Pos >= d.Length) return -1;
            int b = d[Pos];
            if (b != 0xFF) { Pos++; return b; }
            int q = Pos + 1;
            while (q < d.Length && d[q] == 0xFF) q++;
            if (q < d.Length && d[q] == 0) { Pos = q + 1; return 0xFF; }
            UnreadMarker = q < d.Length ? d[q] : 0xD9;
            Pos = q + 1;
            return -1;
        }

        private void Fill(int n)
        {
            while (bits < n)
            {
                int b = NextByte();
                buf = (buf << 8) | (uint)(b < 0 ? 0 : b);
                bits += 8;
            }
        }

        public int GetBits(int n)
        {
            if (n == 0) return 0;
            Fill(n);
            bits -= n;
            return (int)((buf >> bits) & ((1UL << n) - 1));
        }

        public int Peek8() { Fill(8); return (int)((buf >> (bits - 8)) & 0xFF); }

        public void Skip(int n) { bits -= n; }

        public int Decode(LjHuff h)
        {
            int look = h.Look[Peek8()];
            if (look != 0) { Skip(look >> 8); return look & 0xFF; }
            int code = GetBits(8), l = 8;
            while (l < 16 && code > h.MaxCode[l]) { code = (code << 1) | GetBits(1); l++; }
            if (code > h.MaxCode[l]) return 0;   // corrupt: libjpeg returns 0
            return h.Vals[(code + h.ValOffset[l]) & 0xFF];
        }

        // Restart: discard buffered bits, then the RSTn marker.
        public void Restart()
        {
            bits = 0;
            buf = 0;
            if (UnreadMarker == 0)
            {
                while (Pos < d.Length && d[Pos] != 0xFF) Pos++;
                while (Pos < d.Length && d[Pos] == 0xFF) Pos++;
                if (Pos < d.Length) { UnreadMarker = d[Pos]; Pos++; }
            }
            if (UnreadMarker is >= 0xD0 and <= 0xD7) UnreadMarker = 0;
        }

        // Position of the marker that ends the segment (0xFF of it).
        public int EndOfSegment()
        {
            if (UnreadMarker != 0) return Pos - 2;
            int p = Pos;
            while (p + 1 < d.Length && !(d[p] == 0xFF && d[p + 1] != 0 && !(d[p + 1] is >= 0xD0 and <= 0xD7))) p++;
            return p;
        }
    }

    private static readonly int[] LjNatural = BuildLjNatural();

    private static int[] BuildLjNatural()
    {
        int[] zz =
        [
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14,
            21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53,
            60, 61, 54, 47, 55, 62, 63,
        ];
        var n = new int[64 + 16];
        zz.CopyTo(n, 0);
        for (int i = 64; i < n.Length; i++) n[i] = 63;
        return n;
    }

    // jaricom.c jpeg_aritab: (Qe << 16) | (Next_Index_MPS << 8) | (Switch_MPS << 7) | Next_Index_LPS.
    private static readonly int[] LjAritab =
    [
        0x5a1d0181, 0x2586020e, 0x11140310, 0x080b0412, 0x03d80514, 0x01da0617, 0x00e50719, 0x006f081c, 0x0036091e,
        0x001a0a21, 0x000d0b23, 0x00060c09, 0x00030d0a, 0x00010d0c, 0x5a7f0f8f, 0x3f251024, 0x2cf21126, 0x207c1227,
        0x17b91328, 0x1182142a, 0x0cef152b, 0x09a1162d, 0x072f172e, 0x055c1830, 0x04061931, 0x03031a33, 0x02401b34,
        0x01b11c36, 0x01441d38, 0x00f51e39, 0x00b71f3b, 0x008a203c, 0x0068213e, 0x004e223f, 0x003b2320, 0x002c0921,
        0x5ae125a5, 0x484c2640, 0x3a0d2741, 0x2ef12843, 0x261f2944, 0x1f332a45, 0x19a82b46, 0x15182c48, 0x11772d49,
        0x0e742e4a, 0x0bfb2f4b, 0x09f8304d, 0x0861314e, 0x0706324f, 0x05cd3330, 0x04de3432, 0x040f3532, 0x03633633,
        0x02d43734, 0x025c3835, 0x01f83936, 0x01a43a37, 0x01603b38, 0x01253c39, 0x00f63d3a, 0x00cb3e3b, 0x00ab3f3d,
        0x008f203d, 0x5b1241c1, 0x4d044250, 0x412c4351, 0x37d84452, 0x2fe84553, 0x293c4654, 0x23794756, 0x1edf4857,
        0x1aa94957, 0x174e4a48, 0x14244b48, 0x119c4c4a, 0x0f6b4d4a, 0x0d514e4b, 0x0bb64f4d, 0x0a40304d, 0x583251d0,
        0x4d1c5258, 0x438e5359, 0x3bdd545a, 0x34ee555b, 0x2eae565c, 0x299a575d, 0x25164756, 0x557059d8, 0x4ca95a5f,
        0x44d95b60, 0x3e225c61, 0x38245d63, 0x32b45e63, 0x2e17565d, 0x56a860df, 0x4f466165, 0x47e56266, 0x41cf6367,
        0x3c3d6468, 0x375e5d63, 0x52316669, 0x4c0f676a, 0x4639686b, 0x415e6367, 0x56276ae9, 0x50e76b6c, 0x4b85676d,
        0x55976d6e, 0x504f6b6f, 0x5a106fee, 0x55226d70, 0x59eb6ff0, 0x5a1d7171,
    ];

    // Arithmetic decoder state (jdarith.c).
    private sealed class LjArith
    {
        public long C, A;
        public int Ct;
        public readonly byte[][] DcStats = [new byte[64], new byte[64], new byte[64], new byte[64]];
        public readonly byte[][] AcStats = [new byte[256], new byte[256], new byte[256], new byte[256]];
        public readonly byte[] Fixed = new byte[1];

        public int Decode(LjBits src, byte[] stats, int idx)
        {
            while (A < 0x8000)
            {
                if (--Ct < 0)
                {
                    int data = src.NextByte();
                    if (data < 0) data = 0;
                    C = (C << 8) | (uint)data;
                    if ((Ct += 8) < 0)
                        if (++Ct == 0) A = 0x8000;
                }
                A <<= 1;
            }
            int sv = stats[idx];
            long qe = LjAritab[sv & 0x7F];
            int nl = (int)(qe & 0xFF); qe >>= 8;
            int nm = (int)(qe & 0xFF); qe >>= 8;
            long temp = A - qe;
            A = temp;
            temp <<= Ct;
            if (C >= temp)
            {
                C -= temp;
                if (A < qe) { A = qe; stats[idx] = (byte)((sv & 0x80) ^ nm); }
                else { A = qe; stats[idx] = (byte)((sv & 0x80) ^ nl); sv ^= 0x80; }
            }
            else if (A < 0x8000)
            {
                if (A < qe) { stats[idx] = (byte)((sv & 0x80) ^ nl); sv ^= 0x80; }
                else stats[idx] = (byte)((sv & 0x80) ^ nm);
            }
            return sv >> 7;
        }

        public void Reset() { C = 0; A = 0; Ct = -16; }
    }

    /// <summary>The JPEG decoded exactly as libjpeg-turbo's default decompression does; null for what libjpeg-turbo
    /// does not decode (or a malformed file).</summary>
    internal static ImageFrame? ReadLibjpegExact(byte[] d)
    {
        try { return DecodeLj(d); }
        catch (Exception e) when (e is IndexOutOfRangeException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static ImageFrame? DecodeLj(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        int pos = 2;
        int width = 0, height = 0, precision = 8, nComp = 0, maxH = 1, maxV = 1, restartInterval = 0;
        bool progressive = false, arith = false, lossless = false, sawFrame = false, sawJfif = false, sawAdobe = false;
        int adobeTransform = 0;
        LjComp[] comps = [];
        var qt = new int[4][];
        var dcHuff = new LjHuff?[4];
        var acHuff = new LjHuff?[4];
        var dcL = new int[] { 0, 0, 0, 0 };
        var dcU = new int[] { 1, 1, 1, 1 };
        var acK = new int[] { 5, 5, 5, 5 };
        LjArith? ar = null;

        while (true)
        {
            while (pos < d.Length && d[pos] != 0xFF) pos++;
            while (pos < d.Length && d[pos] == 0xFF) pos++;
            if (pos >= d.Length) break;
            int marker = d[pos++];
            if (marker == 0xD9) break;
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue;
            int len = (d[pos] << 8) | d[pos + 1];
            int seg = pos + 2, end = pos + len;
            if (end > d.Length) return null;
            switch (marker)
            {
                case 0xC0: case 0xC1: case 0xC2: case 0xC3: case 0xC9: case 0xCA:
                {
                    if (sawFrame) return null;
                    sawFrame = true;
                    progressive = marker is 0xC2 or 0xCA;
                    arith = marker is 0xC9 or 0xCA;
                    lossless = marker == 0xC3;
                    precision = d[seg];
                    height = (d[seg + 1] << 8) | d[seg + 2];
                    width = (d[seg + 3] << 8) | d[seg + 4];
                    nComp = d[seg + 5];
                    if (width == 0 || height == 0 || nComp is < 1 or > 4) return null;
                    if (lossless ? precision is < 2 or > 16 : precision is not (8 or 12)) return null;
                    comps = new LjComp[nComp];
                    for (int i = 0; i < nComp; i++)
                    {
                        int o = seg + 6 + i * 3;
                        comps[i] = new LjComp { Id = d[o], H = d[o + 1] >> 4, V = d[o + 1] & 15, Tq = d[o + 2] & 3 };
                        if (comps[i].H is < 1 or > 4 || comps[i].V is < 1 or > 4) return null;
                        maxH = Math.Max(maxH, comps[i].H);
                        maxV = Math.Max(maxV, comps[i].V);
                    }
                    int bs = lossless ? 1 : 8;
                    foreach (var c in comps)
                    {
                        c.DownW = (width * c.H + maxH - 1) / maxH;
                        c.DownH = (height * c.V + maxV - 1) / maxV;
                        c.WidthInBlocks = (c.DownW + bs - 1) / bs;
                        c.HeightInBlocks = (c.DownH + bs - 1) / bs;
                        c.AllocW = (c.WidthInBlocks + c.H - 1) / c.H * c.H;
                        c.AllocH = (c.HeightInBlocks + c.V - 1) / c.V * c.V;
                        if (lossless) { c.Diff = new int[c.AllocW * c.AllocH]; c.Samples = new int[c.WidthInBlocks * c.HeightInBlocks]; }
                        else c.Coef = new short[c.AllocW * c.AllocH * 64];
                    }
                    break;
                }
                case 0xC5: case 0xC6: case 0xC7: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
                    return null;   // hierarchical / arithmetic lossless: not in libjpeg-turbo
                case 0xC4:
                {
                    int p = seg;
                    while (p < end)
                    {
                        int tc = d[p] >> 4, th = d[p] & 15;
                        var counts = d.AsSpan(p + 1, 16);
                        int total = 0;
                        foreach (byte b in counts) total += b;
                        if (th > 3 || total > 256) return null;
                        var h = new LjHuff(counts, d.AsSpan(p + 17, total));
                        if (tc == 0) dcHuff[th] = h; else acHuff[th] = h;
                        p += 17 + total;
                    }
                    break;
                }
                case 0xCC:
                {
                    for (int p = seg; p + 1 < end; p += 2)
                    {
                        int tc = d[p] >> 4, tb = d[p] & 15, v = d[p + 1];
                        if (tb > 3) return null;
                        if (tc == 0) { dcL[tb] = v & 15; dcU[tb] = v >> 4; if (dcL[tb] > dcU[tb]) return null; }
                        else { if (v is < 1 or > 63) return null; acK[tb] = v; }
                    }
                    break;
                }
                case 0xDB:
                {
                    int p = seg;
                    while (p < end)
                    {
                        int pq = d[p] >> 4, tq = d[p] & 15;
                        if (tq > 3) return null;
                        var t = new int[64];
                        for (int i = 0; i < 64; i++)
                            t[LjNatural[i]] = pq != 0 ? (d[p + 1 + 2 * i] << 8) | d[p + 2 + 2 * i] : d[p + 1 + i];
                        qt[tq] = t;
                        p += 1 + (pq != 0 ? 128 : 64);
                    }
                    break;
                }
                case 0xDD:
                    restartInterval = (d[seg] << 8) | d[seg + 1];
                    break;
                case 0xE0:
                    if (len >= 16 && d.AsSpan(seg, 5).SequenceEqual("JFIF\0"u8)) sawJfif = true;
                    break;
                case 0xEE:
                    if (len >= 14 && d.AsSpan(seg, 5).SequenceEqual("Adobe"u8)) { sawAdobe = true; adobeTransform = d[seg + 11]; }
                    break;
                case 0xDA:
                {
                    if (!sawFrame) return null;
                    int ns = d[seg];
                    var scomps = new LjComp[ns];
                    for (int i = 0; i < ns; i++)
                    {
                        int cid = d[seg + 1 + i * 2], tbl = d[seg + 2 + i * 2];
                        var c = Array.Find(comps, x => x.Id == cid) ?? throw new InvalidDataException("SOS component");
                        c.DcTbl = tbl >> 4;
                        c.AcTbl = tbl & 15;
                        scomps[i] = c;
                    }
                    int so = seg + 1 + ns * 2;
                    int ss = d[so], se = d[so + 1], ah = d[so + 2] >> 4, al = d[so + 2] & 15;
                    var bits = new LjBits(d, end);
                    if (lossless)
                        DecodeLosslessScan(bits, scomps, dcHuff, width, height, maxH, maxV, restartInterval, ss, al, precision);
                    else if (arith)
                        DecodeArithScan(bits, ar ??= new LjArith(), scomps, width, height, maxH, maxV, restartInterval,
                            progressive, ss, se, ah, al, dcL, dcU, acK);
                    else
                        DecodeHuffScan(bits, scomps, dcHuff, acHuff, width, height, maxH, maxV, restartInterval,
                            progressive, ss, se, ah, al);
                    pos = bits.EndOfSegment();
                    continue;
                }
            }
            pos = end;
        }
        if (!sawFrame) return null;

        // Colour space (jdapimin.c default_decompress_parms).
        int space; // 0 grey, 1 YCbCr, 2 RGB, 3 CMYK, 4 YCCK
        switch (nComp)
        {
            case 1: space = 0; break;
            case 3:
                if (sawJfif) space = 1;
                else if (sawAdobe) space = adobeTransform == 0 ? 2 : 1;
                else if (comps[0].Id == 1 && comps[1].Id == 2 && comps[2].Id == 3) space = lossless ? 2 : 1;
                else if (comps[0].Id == 82 && comps[1].Id == 71 && comps[2].Id == 66) space = 2;
                else space = lossless ? 2 : 1;
                break;
            case 4:
                space = sawAdobe ? (adobeTransform == 0 ? 3 : 4) : 3;
                break;
            default: return null;
        }
        if (lossless && space is 1 or 4) return null;   // libjpeg: no lossy colour conversion in lossless mode

        // Component planes at their downsampled size.
        int max = lossless ? (1 << precision) - 1 : precision == 12 ? 4095 : 255;
        var planes = new int[nComp][];
        for (int ci = 0; ci < nComp; ci++)
        {
            var c = comps[ci];
            if (lossless) { planes[ci] = c.Samples; continue; }
            var t = qt[c.Tq] ?? throw new InvalidDataException("missing DQT");
            var plane = new int[c.DownW * c.DownH];
            Span<int> blk = stackalloc int[64];
            for (int by = 0; by < c.HeightInBlocks; by++)
                for (int bx = 0; bx < c.WidthInBlocks; bx++)
                {
                    LjIdctIslow(c.Coef.AsSpan((by * c.AllocW + bx) * 64, 64), t, blk, precision);
                    for (int y = 0; y < 8 && by * 8 + y < c.DownH; y++)
                        for (int x = 0; x < 8 && bx * 8 + x < c.DownW; x++)
                            plane[(by * 8 + y) * c.DownW + bx * 8 + x] = blk[y * 8 + x];
                }
            planes[ci] = plane;
        }

        // Upsampling to full size.
        var full = new int[nComp][];
        for (int ci = 0; ci < nComp; ci++)
            full[ci] = LjUpsample(planes[ci], comps[ci], width, height, maxH, maxV, !lossless) ?? throw new NotSupportedException("sampling");

        // Colour conversion + output.
        var frame = new ImageFrame();
        frame.Initialize(width, height, space >= 3 ? ColorspaceType.CMYK : ColorspaceType.SRGB, false);
        int nch = frame.NumberOfChannels;
        int[]? crR = null, cbB = null, crG = null, cbG = null;
        if (space is 1 or 4) LjYccTables(max, out crR, out cbB, out crG, out cbG);
        int center = (max + 1) / 2;
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x, o = x * nch;
                switch (space)
                {
                    case 0:
                        row[o] = row[o + 1] = row[o + 2] = Scale16(full[0][i], max);
                        break;
                    case 2:
                        row[o] = Scale16(full[0][i], max);
                        row[o + 1] = Scale16(full[1][i], max);
                        row[o + 2] = Scale16(full[2][i], max);
                        break;
                    case 1:
                    {
                        int yy = full[0][i], cb = full[1][i], cr = full[2][i];
                        row[o] = Scale16(Math.Clamp(yy + crR![cr], 0, max), max);
                        row[o + 1] = Scale16(Math.Clamp(yy + ((cbG![cb] + crG![cr]) >> 16), 0, max), max);
                        row[o + 2] = Scale16(Math.Clamp(yy + cbB![cb], 0, max), max);
                        break;
                    }
                    default:
                    {
                        int c0, c1, c2;
                        if (space == 4)
                        {
                            int yy = full[0][i], cb = full[1][i], cr = full[2][i];
                            c0 = Math.Clamp(max - (yy + crR![cr]), 0, max);
                            c1 = Math.Clamp(max - (yy + ((cbG![cb] + crG![cr]) >> 16)), 0, max);
                            c2 = Math.Clamp(max - (yy + cbB![cb]), 0, max);
                        }
                        else { c0 = full[0][i]; c1 = full[1][i]; c2 = full[2][i]; }
                        int k = full[3][i];
                        // Adobe (Photoshop) CMYK JPEGs store inverted samples: flip to ink amounts.
                        if (sawAdobe) { c0 = max - c0; c1 = max - c1; c2 = max - c2; k = max - k; }
                        row[o] = Scale16(c0, max);
                        row[o + 1] = Scale16(c1, max);
                        row[o + 2] = Scale16(c2, max);
                        row[o + 3] = Scale16(k, max);
                        break;
                    }
                }
            }
        }
        return frame;

        static ushort Scale16(int v, int max) => (ushort)(((long)v * 65535 + max / 2) / max);
    }

    // jdcolor.c build_ycc_rgb_table for MAXJSAMPLE = max (SCALEBITS 16).
    private static void LjYccTables(int max, out int[] crR, out int[] cbB, out int[] crG, out int[] cbG)
    {
        const int scale = 16, half = 1 << (scale - 1);
        static int Fix(double v) => (int)(v * (1 << scale) + 0.5);
        int n = max + 1, center = n / 2;
        crR = new int[n]; cbB = new int[n]; crG = new int[n]; cbG = new int[n];
        for (int i = 0; i < n; i++)
        {
            int x = i - center;
            crR[i] = (Fix(1.40200) * x + half) >> scale;
            cbB[i] = (Fix(1.77200) * x + half) >> scale;
            crG[i] = -Fix(0.71414) * x;
            cbG[i] = -Fix(0.34414) * x + half;
        }
    }

    // ── Huffman (jdhuff.c sequential, jdphuff.c progressive) ────────────────────────────────────────────────────
    private static int LjExtend(int r, int s) => r < (1 << (s - 1)) ? r + (-1 << s) + 1 : r;

    private static void DecodeHuffScan(LjBits b, LjComp[] sc, LjHuff?[] dcH, LjHuff?[] acH, int width, int height,
        int maxH, int maxV, int restartInterval, bool progressive, int ss, int se, int ah, int al)
    {
        foreach (var c in sc) c.LastDc = 0;
        int eobrun = 0;
        int restartsToGo = restartInterval;
        foreach (var (c, blk) in LjBlocks(sc, width, height, maxH, maxV, () =>
        {
            if (restartInterval == 0) return;
            if (restartsToGo == 0)
            {
                b.Restart();
                foreach (var cc in sc) cc.LastDc = 0;
                eobrun = 0;
                restartsToGo = restartInterval;
            }
            restartsToGo--;
        }))
        {
            var coef = c.Coef.AsSpan(blk * 64, 64);
            if (!progressive)
            {
                int s = b.Decode(dcH[c.DcTbl] ?? throw new InvalidDataException("DHT"));
                if (s != 0) s = LjExtend(b.GetBits(s), s);
                c.LastDc += s;
                coef[0] = (short)c.LastDc;
                var ac = acH[c.AcTbl] ?? throw new InvalidDataException("DHT");
                for (int k = 1; k < 64; k++)
                {
                    s = b.Decode(ac);
                    int r = s >> 4;
                    s &= 15;
                    if (s != 0)
                    {
                        k += r;
                        coef[LjNatural[k]] = (short)LjExtend(b.GetBits(s), s);
                    }
                    else
                    {
                        if (r != 15) break;
                        k += 15;
                    }
                }
                continue;
            }
            if (ss == 0)
            {
                if (ah == 0)
                {
                    int s = b.Decode(dcH[c.DcTbl] ?? throw new InvalidDataException("DHT"));
                    if (s != 0) s = LjExtend(b.GetBits(s), s);
                    c.LastDc += s;
                    coef[0] = (short)(c.LastDc << al);
                }
                else if (b.GetBits(1) != 0) coef[0] |= (short)(1 << al);
                continue;
            }
            var acT = acH[c.AcTbl] ?? throw new InvalidDataException("DHT");
            if (ah == 0)
            {
                if (eobrun > 0) { eobrun--; continue; }
                for (int k = ss; k <= se; k++)
                {
                    int s = b.Decode(acT);
                    int r = s >> 4;
                    s &= 15;
                    if (s != 0)
                    {
                        k += r;
                        coef[LjNatural[k]] = (short)(LjExtend(b.GetBits(s), s) << al);
                    }
                    else
                    {
                        if (r == 15) { k += 15; continue; }
                        eobrun = 1 << r;
                        if (r != 0) eobrun += b.GetBits(r);
                        eobrun--;
                        break;
                    }
                }
                continue;
            }
            {
                int p1 = 1 << al, m1 = -1 << al;
                int k = ss;
                if (eobrun == 0)
                {
                    for (; k <= se; k++)
                    {
                        int s = b.Decode(acT);
                        int r = s >> 4;
                        s &= 15;
                        if (s != 0) s = b.GetBits(1) != 0 ? p1 : m1;
                        else if (r != 15)
                        {
                            eobrun = 1 << r;
                            if (r != 0) eobrun += b.GetBits(r);
                            break;
                        }
                        do
                        {
                            int pos = LjNatural[k];
                            if (coef[pos] != 0)
                            {
                                if (b.GetBits(1) != 0 && (coef[pos] & p1) == 0)
                                    coef[pos] = (short)(coef[pos] >= 0 ? coef[pos] + p1 : coef[pos] + m1);
                            }
                            else if (--r < 0) break;
                            k++;
                        } while (k <= se);
                        if (s != 0) coef[LjNatural[k]] = (short)s;
                    }
                }
                if (eobrun > 0)
                {
                    for (; k <= se; k++)
                    {
                        int pos = LjNatural[k];
                        if (coef[pos] != 0 && b.GetBits(1) != 0 && (coef[pos] & p1) == 0)
                            coef[pos] = (short)(coef[pos] >= 0 ? coef[pos] + p1 : coef[pos] + m1);
                    }
                    eobrun--;
                }
            }
        }
    }

    // Iterates the scan's blocks in MCU order (jdinput per_scan_setup geometry), calling beforeMcu first for each MCU:
    // (component, block index in its coefficient array). A single-component scan covers only the component's own
    // blocks; an interleaved scan every block of each MCU (padding blocks included).
    private static IEnumerable<(LjComp C, int Blk)> LjBlocks(LjComp[] sc, int width, int height, int maxH, int maxV, Action beforeMcu)
    {
        if (sc.Length == 1)
        {
            var c = sc[0];
            for (int y = 0; y < c.HeightInBlocks; y++)
                for (int x = 0; x < c.WidthInBlocks; x++)
                {
                    beforeMcu();
                    yield return (c, y * c.AllocW + x);
                }
            yield break;
        }
        int mcusX = (width + 8 * maxH - 1) / (8 * maxH), mcusY = (height + 8 * maxV - 1) / (8 * maxV);
        for (int my = 0; my < mcusY; my++)
            for (int mx = 0; mx < mcusX; mx++)
            {
                beforeMcu();
                foreach (var c in sc)
                    for (int v = 0; v < c.V; v++)
                        for (int h = 0; h < c.H; h++)
                            yield return (c, (my * c.V + v) * c.AllocW + mx * c.H + h);
            }
    }

    // ── Arithmetic (jdarith.c) ───────────────────────────────────────────────────────────────────────────────────
    private static void DecodeArithScan(LjBits b, LjArith e, LjComp[] sc, int width, int height, int maxH, int maxV,
        int restartInterval, bool progressive, int ss, int se, int ah, int al, int[] dcL, int[] dcU, int[] acK)
    {
        void InitStats()
        {
            foreach (var c in sc)
            {
                if (!progressive || (ss == 0 && ah == 0)) { Array.Clear(e.DcStats[c.DcTbl]); c.LastDc = 0; c.DcContext = 0; }
                if (!progressive || ss != 0) Array.Clear(e.AcStats[c.AcTbl]);
            }
            e.Reset();
        }
        InitStats();
        e.Fixed[0] = 113;
        int restartsToGo = restartInterval;
        bool error = false;
        foreach (var (c, blk) in LjBlocks(sc, width, height, maxH, maxV, () =>
        {
            if (restartInterval == 0) return;
            if (restartsToGo == 0)
            {
                b.Restart();
                InitStats();
                error = false;
                restartsToGo = restartInterval;
            }
            restartsToGo--;
        }))
        {
            if (error && !(progressive && ss == 0 && ah != 0)) continue;
            var coef = c.Coef.AsSpan(blk * 64, 64);
            if (!progressive || ss == 0)
            {
                if (progressive && ah != 0)
                {
                    if (e.Decode(b, e.Fixed, 0) != 0) coef[0] |= (short)(1 << al);
                    continue;
                }
                // DC (Figures F.19 - F.24).
                var st = e.DcStats[c.DcTbl];
                int si = c.DcContext;
                if (e.Decode(b, st, si) == 0) c.DcContext = 0;
                else
                {
                    int sign = e.Decode(b, st, si + 1);
                    si += 2 + sign;
                    int m = e.Decode(b, st, si);
                    if (m != 0)
                    {
                        si = 20;
                        while (e.Decode(b, st, si) != 0)
                        {
                            if ((m <<= 1) == 0x8000) { error = true; break; }
                            si++;
                        }
                        if (error) continue;
                    }
                    if (m < (int)((1L << dcL[c.DcTbl]) >> 1)) c.DcContext = 0;
                    else if (m > (int)((1L << dcU[c.DcTbl]) >> 1)) c.DcContext = 12 + sign * 4;
                    else c.DcContext = 4 + sign * 4;
                    int v = m;
                    si += 14;
                    while ((m >>= 1) != 0)
                        if (e.Decode(b, st, si) != 0) v |= m;
                    v += 1;
                    if (sign != 0) v = -v;
                    c.LastDc = (c.LastDc + v) & 0xffff;
                }
                coef[0] = (short)(progressive ? c.LastDc << al : c.LastDc);
                if (progressive) continue;
            }
            // AC.
            var ast = e.AcStats[c.AcTbl];
            int k0 = progressive ? ss : 1, k1 = progressive ? se : 63;
            if (progressive && ah != 0)
            {
                int p1 = 1 << al, m1 = -1 << al;
                int kex;
                for (kex = se; kex > 0; kex--) if (coef[LjNatural[kex]] != 0) break;
                for (int k = ss; k <= se; k++)
                {
                    int si = 3 * (k - 1);
                    if (k > kex && e.Decode(b, ast, si) != 0) break;
                    for (;;)
                    {
                        int pos = LjNatural[k];
                        if (coef[pos] != 0)
                        {
                            if (e.Decode(b, ast, si + 2) != 0)
                                coef[pos] = (short)(coef[pos] < 0 ? coef[pos] + m1 : coef[pos] + p1);
                            break;
                        }
                        if (e.Decode(b, ast, si + 1) != 0)
                        {
                            coef[pos] = (short)(e.Decode(b, e.Fixed, 0) != 0 ? m1 : p1);
                            break;
                        }
                        si += 3;
                        k++;
                        if (k > se) { error = true; break; }
                    }
                    if (error) break;
                }
                continue;
            }
            for (int k = k0; k <= k1; k++)
            {
                int si = 3 * (k - 1);
                if (e.Decode(b, ast, si) != 0) break;
                while (e.Decode(b, ast, si + 1) == 0)
                {
                    si += 3;
                    k++;
                    if (k > k1) { error = true; break; }
                }
                if (error) break;
                int sign = e.Decode(b, e.Fixed, 0);
                si += 2;
                int m = e.Decode(b, ast, si);
                if (m != 0 && e.Decode(b, ast, si) != 0)
                {
                    m <<= 1;
                    si = k <= acK[c.AcTbl] ? 189 : 217;
                    while (e.Decode(b, ast, si) != 0)
                    {
                        if ((m <<= 1) == 0x8000) { error = true; break; }
                        si++;
                    }
                    if (error) break;
                }
                int v = m;
                si += 14;
                while ((m >>= 1) != 0)
                    if (e.Decode(b, ast, si) != 0) v |= m;
                v += 1;
                if (sign != 0) v = -v;
                coef[LjNatural[k]] = (short)(progressive ? (int)((uint)v << al) : v);
            }
        }
    }

    // ── Lossless (jdlhuff.c, jddiffct.c, jdlossls.c) ─────────────────────────────────────────────────────────────
    private static void DecodeLosslessScan(LjBits b, LjComp[] sc, LjHuff?[] dcH, int width, int height, int maxH, int maxV,
        int restartInterval, int psv, int pt, int precision)
    {
        if (psv is < 1 or > 7 || pt >= precision) throw new InvalidDataException("lossless scan parameters");
        bool single = sc.Length == 1;
        int mcusX = single ? sc[0].WidthInBlocks : (width + maxH - 1) / maxH;
        int mcusY = single ? sc[0].HeightInBlocks : (height + maxV - 1) / maxV;
        if (restartInterval != 0 && restartInterval % mcusX != 0) throw new NotSupportedException("restart interval");
        int restartRows = restartInterval / Math.Max(1, mcusX);
        var restartAt = new bool[mcusY];
        int rowsToGo = restartRows;
        for (int my = 0; my < mcusY; my++)
        {
            if (restartInterval != 0)
            {
                if (rowsToGo == 0) { b.Restart(); restartAt[my] = true; rowsToGo = restartRows; }
                rowsToGo--;
            }
            for (int mx = 0; mx < mcusX; mx++)
                foreach (var c in sc)
                {
                    int hh = single ? 1 : c.H, vv = single ? 1 : c.V;
                    var t = dcH[c.DcTbl] ?? throw new InvalidDataException("DHT");
                    for (int v = 0; v < vv; v++)
                        for (int h = 0; h < hh; h++)
                        {
                            int s = b.Decode(t);
                            if (s != 0) s = s == 16 ? 32768 : LjExtend(b.GetBits(s), s);
                            c.Diff[(my * vv + v) * c.AllocW + mx * hh + h] = s;
                        }
                }
        }
        // Undifference + point-transform scaling per component row; the first row after the start / a restart uses
        // the 1-D initial predictor.
        int mask = precision <= 8 ? 0xFF : 0xFFFF;
        foreach (var c in sc)
        {
            int vv = single ? 1 : c.V, w = c.WidthInBlocks;
            var prev = new int[w];
            var cur = new int[w];
            bool firstRow = true;
            for (int y = 0; y < c.HeightInBlocks; y++)
            {
                if (y % vv == 0 && restartAt[y / vv]) firstRow = true;
                int o = y * c.AllocW;
                if (firstRow)
                {
                    int ra = (c.Diff[o] + (1 << (precision - pt - 1))) & 0xFFFF;
                    cur[0] = ra;
                    for (int x = 1; x < w; x++) cur[x] = ra = (c.Diff[o + x] + ra) & 0xFFFF;
                    firstRow = false;
                }
                else
                {
                    int rb = prev[0];
                    int ra = (c.Diff[o] + rb) & 0xFFFF;
                    cur[0] = ra;
                    for (int x = 1; x < w; x++)
                    {
                        int rc = rb;
                        rb = prev[x];
                        int pred = psv switch
                        {
                            1 => ra,
                            2 => rb,
                            3 => rc,
                            4 => ra + rb - rc,
                            5 => ra + ((rb - rc) >> 1),
                            6 => rb + ((ra - rc) >> 1),
                            _ => (ra + rb) >> 1,
                        };
                        cur[x] = ra = (c.Diff[o + x] + pred) & 0xFFFF;
                    }
                }
                for (int x = 0; x < w; x++) c.Samples[y * w + x] = (cur[x] << pt) & mask;
                (prev, cur) = (cur, prev);
            }
        }
    }

    // ── IDCT (jidctint.c jpeg_idct_islow) at 8 / 12 bits, with the post-IDCT range-limit table ─────────────────────
    private static void LjIdctIslow(ReadOnlySpan<short> coef, int[] qt, Span<int> output, int precision)
    {
        const int CB = 13;
        int p1 = precision == 12 ? 1 : 2;
        int n = precision == 12 ? 4096 : 256, center = n / 2, mask = 4 * n - 1, maxv = n - 1;
        const int F0_298 = 2446, F0_390 = 3196, F0_541 = 4433, F0_765 = 6270, F0_899 = 7373, F1_175 = 9633,
            F1_501 = 12299, F1_847 = 15137, F1_961 = 16069, F2_053 = 16819, F2_562 = 20995, F3_072 = 25172;
        Span<int> ws = stackalloc int[64];
        unchecked
        {
            for (int col = 0; col < 8; col++)
            {
                if (coef[8 + col] == 0 && coef[16 + col] == 0 && coef[24 + col] == 0 && coef[32 + col] == 0 &&
                    coef[40 + col] == 0 && coef[48 + col] == 0 && coef[56 + col] == 0)
                {
                    int dc = (coef[col] * qt[col]) << p1;
                    for (int r = 0; r < 8; r++) ws[r * 8 + col] = dc;
                    continue;
                }
                int z2 = coef[16 + col] * qt[16 + col], z3 = coef[48 + col] * qt[48 + col];
                int z1 = (z2 + z3) * F0_541;
                int tmp2 = z1 + z3 * -F1_847, tmp3 = z1 + z2 * F0_765;
                z2 = coef[col] * qt[col];
                z3 = coef[32 + col] * qt[32 + col];
                int tmp0 = (z2 + z3) << CB, tmp1 = (z2 - z3) << CB;
                int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
                tmp0 = coef[56 + col] * qt[56 + col];
                tmp1 = coef[40 + col] * qt[40 + col];
                tmp2 = coef[24 + col] * qt[24 + col];
                tmp3 = coef[8 + col] * qt[8 + col];
                Odd(ref tmp0, ref tmp1, ref tmp2, ref tmp3);
                int s = CB - p1;
                ws[col] = Descale(tmp10 + tmp3, s);
                ws[56 + col] = Descale(tmp10 - tmp3, s);
                ws[8 + col] = Descale(tmp11 + tmp2, s);
                ws[48 + col] = Descale(tmp11 - tmp2, s);
                ws[16 + col] = Descale(tmp12 + tmp1, s);
                ws[40 + col] = Descale(tmp12 - tmp1, s);
                ws[24 + col] = Descale(tmp13 + tmp0, s);
                ws[32 + col] = Descale(tmp13 - tmp0, s);
            }
            for (int row = 0; row < 8; row++)
            {
                int o = row * 8;
                int s = CB + p1 + 3;
                int z2 = ws[o + 2], z3 = ws[o + 6];
                int z1 = (z2 + z3) * F0_541;
                int tmp2 = z1 + z3 * -F1_847, tmp3 = z1 + z2 * F0_765;
                int tmp0 = (ws[o] + ws[o + 4]) << CB, tmp1 = (ws[o] - ws[o + 4]) << CB;
                int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
                tmp0 = ws[o + 7]; tmp1 = ws[o + 5]; tmp2 = ws[o + 3]; tmp3 = ws[o + 1];
                Odd(ref tmp0, ref tmp1, ref tmp2, ref tmp3);
                output[o] = Range(Descale(tmp10 + tmp3, s));
                output[o + 7] = Range(Descale(tmp10 - tmp3, s));
                output[o + 1] = Range(Descale(tmp11 + tmp2, s));
                output[o + 6] = Range(Descale(tmp11 - tmp2, s));
                output[o + 2] = Range(Descale(tmp12 + tmp1, s));
                output[o + 5] = Range(Descale(tmp12 - tmp1, s));
                output[o + 3] = Range(Descale(tmp13 + tmp0, s));
                output[o + 4] = Range(Descale(tmp13 - tmp0, s));
            }
        }

        static void Odd(ref int t0, ref int t1, ref int t2, ref int t3)
        {
            unchecked
            {
                int z1 = t0 + t3, z2 = t1 + t2, z3 = t0 + t2, z4 = t1 + t3;
                int z5 = (z3 + z4) * F1_175;
                t0 *= F0_298; t1 *= F2_053; t2 *= F3_072; t3 *= F1_501;
                z1 *= -F0_899; z2 *= -F2_562; z3 *= -F1_961; z4 *= -F0_390;
                z3 += z5; z4 += z5;
                t0 += z1 + z3; t1 += z2 + z4; t2 += z2 + z3; t3 += z1 + z4;
            }
        }
        static int Descale(int x, int s) => unchecked((x + (1 << (s - 1))) >> s);
        // jdmaster.c prepare_range_limit_table, IDCT part: x & (4N-1) -> [0,C): x+C; [C,2N): max; [2N,4N-C): 0; else x-(4N-C).
        int Range(int v)
        {
            int x = v & mask;
            return x < center ? x + center : x < 2 * n ? maxv : x < 4 * n - center ? 0 : x - (4 * n - center);
        }
    }

    // ── Upsampling (jdsample.c) ──────────────────────────────────────────────────────────────────────────────────
    private static int[]? LjUpsample(int[] src, LjComp c, int w, int h, int maxH, int maxV, bool fancy)
    {
        int sw = c.DownW, sh = c.DownH;
        if (maxH % c.H != 0 || maxV % c.V != 0) return null;
        int rx = maxH / c.H, ry = maxV / c.V;
        if (rx == 1 && ry == 1) return src;
        var dst = new int[w * h];
        if (fancy && rx == 2 && ry == 1 && sw > 2)
        {
            var line = new int[sw * 2];
            for (int y = 0; y < h; y++)
            {
                int o = y * sw, v = src[o];
                line[0] = v;
                line[1] = (v * 3 + src[o + 1] + 2) >> 2;
                for (int x = 1; x < sw - 1; x++)
                {
                    v = src[o + x] * 3;
                    line[2 * x] = (v + src[o + x - 1] + 1) >> 2;
                    line[2 * x + 1] = (v + src[o + x + 1] + 2) >> 2;
                }
                v = src[o + sw - 1];
                line[2 * sw - 2] = (v * 3 + src[o + sw - 2] + 1) >> 2;
                line[2 * sw - 1] = v;
                Array.Copy(line, 0, dst, y * w, w);
            }
            return dst;
        }
        if (fancy && rx == 1 && ry == 2)
        {
            for (int y = 0; y < h; y++)
            {
                int r0 = y >> 1, r1 = Math.Clamp((y & 1) == 0 ? r0 - 1 : r0 + 1, 0, sh - 1), bias = (y & 1) == 0 ? 1 : 2;
                for (int x = 0; x < w; x++) dst[y * w + x] = (src[r0 * sw + x] * 3 + src[r1 * sw + x] + bias) >> 2;
            }
            return dst;
        }
        if (fancy && rx == 2 && ry == 2 && sw > 2)
        {
            var line = new int[sw * 2];
            for (int y = 0; y < h; y++)
            {
                int r0 = y >> 1, r1 = Math.Clamp((y & 1) == 0 ? r0 - 1 : r0 + 1, 0, sh - 1);
                int o0 = r0 * sw, o1 = r1 * sw;
                int thisSum = src[o0] * 3 + src[o1], nextSum = src[o0 + 1] * 3 + src[o1 + 1];
                line[0] = (thisSum * 4 + 8) >> 4;
                line[1] = (thisSum * 3 + nextSum + 7) >> 4;
                int lastSum = thisSum;
                thisSum = nextSum;
                for (int x = 1; x < sw - 1; x++)
                {
                    nextSum = src[o0 + x + 1] * 3 + src[o1 + x + 1];
                    line[2 * x] = (thisSum * 3 + lastSum + 8) >> 4;
                    line[2 * x + 1] = (thisSum * 3 + nextSum + 7) >> 4;
                    lastSum = thisSum;
                    thisSum = nextSum;
                }
                line[2 * sw - 2] = (thisSum * 3 + lastSum + 8) >> 4;
                line[2 * sw - 1] = (thisSum * 4 + 7) >> 4;
                Array.Copy(line, 0, dst, y * w, w);
            }
            return dst;
        }
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(y / ry, sh - 1);
            for (int x = 0; x < w; x++) dst[y * w + x] = src[sy * sw + Math.Min(x / rx, sw - 1)];
        }
        return dst;
    }
}
