using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

// A port of libjpeg-turbo 3.1's default decompression, pixel-exact: baseline / extended / progressive DCT with Huffman
// or arithmetic entropy coding (SOF0/1/2/9/10) at 8 or 12 bits, lossless (SOF3, precision 2..16, predictors 1..7,
// point transform), the accurate integer IDCT (jidctint.c islow) with its range-limit table, jdsample.c's upsampler
// choice (fancy h2v1 / h1v2 / h2v2, else replication), jdcolor.c's fixed-point YCbCr -> RGB and YCCK -> CMYK, and
// jdapimin.c's colour-space guess (JFIF / Adobe APP14 / component ids). Output: grey and RGB as sRGB frames, CMYK /
// YCCK as CMYK frames (Adobe-inverted samples flipped to ink amounts), samples scaled from the file's range to 16 bits.
// Malformed files fail where libjpeg-turbo fails (InvalidDataException, its error text); what libjpeg-turbo cannot
// decode (hierarchical, arithmetic lossless, lossless colour conversion, fractional sampling) is NotSupportedException.
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
        public int[]? Qt;              // quantization table latched at the component's first scan (jdinput.c)
        // Progressive status (cinfo->coef_bits): the successive-approximation bit each coefficient is known to, -1 when
        // never scanned; PrevBits as of the previous scan (for rows an incomplete last scan did not reach).
        public readonly int[] CurBits = Enumerable.Repeat(-1, 64).ToArray();
        public readonly int[] PrevBits = new int[64];
        public readonly int[] NoBits = Enumerable.Repeat(-1, 64).ToArray();
    }

    private sealed class LjHuff
    {
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValOffset = new int[18];
        public readonly byte[] Vals = new byte[256];
        public readonly int[] Look = new int[256];   // (length << 8) | value, 0 = not in the lookahead
        public readonly bool Bad;                     // code lengths do not form a prefix code (JERR_BAD_HUFF_TABLE)
        public readonly int MaxSymbol;

        public LjHuff(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> vals)
        {
            vals.CopyTo(Vals);
            foreach (byte v in vals) MaxSymbol = Math.Max(MaxSymbol, v);
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
                // No code may be all ones: the next code must still fit in si bits.
                if (c >= 1 << si) { Bad = true; return; }
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
    // marker ends the data (zeros are supplied from then on, as libjpeg does). Insufficient mirrors libjpeg's
    // entropy->insufficient_data: set once a decoder consumes bits past the real data (jdhuff.c jpeg_fill_bit_buffer),
    // after which the Huffman decoders leave MCUs untouched until a restart marker resynchronises.
    private sealed class LjBits
    {
        private readonly byte[] d;
        public int Pos;
        public int UnreadMarker;
        public bool Insufficient;
        public int LastGoodRow;   // cinfo->master->last_good_iMCU_row: iMCU row of the last MCU started with data left
        private int nextRestartNum;
        private ulong buf;
        private int bits;
        private int padBits;   // zero bits appended after the data ended (the low end of buf)

        public LjBits(byte[] data, int pos) { d = data; Pos = pos; }

        // Next data byte (0xFF00 -> 0xFF), or -1 at a marker (left unread; the end of the data reads as EOI, as
        // libjpeg's source manager inserts one).
        public int NextByte()
        {
            if (UnreadMarker != 0) return -1;
            if (Pos >= d.Length) { UnreadMarker = 0xD9; return -1; }
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
                if (b < 0) padBits += 8;
            }
        }

        // Consuming n bits; reaching into the padding means the data ran out.
        private void Consume(int n)
        {
            bits -= n;
            if (bits < padBits) { Insufficient = true; padBits = bits; }
        }

        public int GetBits(int n)
        {
            if (n == 0) return 0;
            Fill(n);
            Consume(n);
            return (int)((buf >> bits) & ((1UL << n) - 1));
        }

        public int Peek8() { Fill(8); return (int)((buf >> (bits - 8)) & 0xFF); }

        public void Skip(int n) { Consume(n); }

        public int Decode(LjHuff h)
        {
            int look = h.Look[Peek8()];
            if (look != 0) { Skip(look >> 8); return look & 0xFF; }
            int code = GetBits(8), l = 8;
            // jdhuff.c jpeg_huff_decode: MaxCode[17] is a sentinel, so a corrupt code consumes 17 bits, then decodes as 0.
            while (code > h.MaxCode[l]) { code = (code << 1) | GetBits(1); l++; }
            if (l > 16) return 0;
            return h.Vals[(code + h.ValOffset[l]) & 0xFF];
        }

        // jdhuff.c process_restart + jdmarker.c read_restart_marker: discard buffered bits, then the RSTn marker; a
        // wrong or missing marker resynchronises as jpeg_resync_to_restart does. The data-exhausted state clears unless
        // the resync left us at a marker (the next segment then counts as empty).
        public void Restart()
        {
            bits = 0;
            buf = 0;
            padBits = 0;
            if (UnreadMarker == 0) NextMarker();
            if (UnreadMarker == 0xD0 + nextRestartNum) UnreadMarker = 0;
            else Resync(nextRestartNum);
            nextRestartNum = (nextRestartNum + 1) & 7;
            if (UnreadMarker == 0) Insufficient = false;
        }

        // jdmarker.c next_marker: skip anything up to 0xFF, the 0xFF fill bytes, and stuffed 0xFF00 pairs.
        private void NextMarker()
        {
            for (;;)
            {
                while (Pos < d.Length && d[Pos] != 0xFF) Pos++;
                while (Pos < d.Length && d[Pos] == 0xFF) Pos++;
                if (Pos >= d.Length) { UnreadMarker = 0xD9; return; }
                int c = d[Pos++];
                if (c != 0) { UnreadMarker = c; return; }
            }
        }

        // jdmarker.c jpeg_resync_to_restart.
        private void Resync(int desired)
        {
            for (;;)
            {
                int marker = UnreadMarker, action;
                if (marker < 0xC0) action = 2;                                     // invalid marker
                else if (marker is < 0xD0 or > 0xD7) action = 3;                    // valid non-restart marker
                else if (marker == 0xD0 + ((desired + 1) & 7) || marker == 0xD0 + ((desired + 2) & 7)) action = 3;
                else if (marker == 0xD0 + ((desired - 1) & 7) || marker == 0xD0 + ((desired - 2) & 7)) action = 2;
                else action = 1;                                                    // desired restart or too far away
                if (action == 1) { UnreadMarker = 0; return; }
                if (action == 3) return;
                NextMarker();
            }
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
        public readonly byte[][] DcStats = [.. Enumerable.Range(0, 16).Select(_ => new byte[64])];
        public readonly byte[][] AcStats = [.. Enumerable.Range(0, 16).Select(_ => new byte[256])];
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

    /// <summary>The JPEG decoded exactly as libjpeg-turbo's default decompression does. Malformed data fails as
    /// libjpeg-turbo's ERREXIT does (<see cref="InvalidDataException"/> with its message); what libjpeg-turbo cannot
    /// decode either (hierarchical, arithmetic lossless, fractional sampling, ...) throws
    /// <see cref="NotSupportedException"/>. Entropy-coded data errors are recovered from as libjpeg-turbo does (its
    /// warnings: corrupt codes decode as 0, missing data as zeros).</summary>
    internal static ImageFrame ReadLibjpegExact(byte[] d, long maxPixels = DefaultMaxPixels) => DecodeLj(d, maxPixels);

    /// <summary>Default pixel limit of <see cref="Read(Stream)"/>: 16384 x 16384, as libavif's image size limit. A
    /// larger frame header fails before its buffers are allocated (libjpeg-turbo would try to allocate them).</summary>
    public const long DefaultMaxPixels = 16384L * 16384;

    // jdatasrc.c fill_input_buffer at the end of the data: FF D9 (a fake EOI), again on every further read.
    private static byte[] PadWithFakeEoi(byte[] d, long need)
    {
        var r = new byte[(int)Math.Min(need + 2, int.MaxValue)];
        d.CopyTo(r, 0);
        for (int i = d.Length; i < r.Length; i++) r[i] = ((i - d.Length) & 1) == 0 ? (byte)0xFF : (byte)0xD9;
        return r;
    }

    private static ImageFrame DecodeLj(byte[] d, long maxPixels)
    {
        // jdmarker.c first_marker: JERR_NO_SOI.
        if (d.Length < 2 || d[0] != 0xFF || d[1] != 0xD8)
            throw new InvalidDataException($"Not a JPEG file: starts with 0x{(d.Length > 0 ? d[0] : 0):x2} 0x{(d.Length > 1 ? d[1] : 0):x2}");
        int pos = 2;
        int width = 0, height = 0, precision = 8, nComp = 0, maxH = 1, maxV = 1, restartInterval = 0;
        bool progressive = false, arith = false, lossless = false, sawFrame = false, sawJfif = false, sawAdobe = false;
        bool entropyStarted = false;   // jpeg_start_decompress ran (first SOS reached): default Huffman tables installed
        int scans = 0, lastGoodRow = 0;
        bool multiScan = false;
        int adobeTransform = 0;
        LjComp[] comps = [];
        var qt = new int[4][];
        var dcHuff = new LjHuff?[4];
        var acHuff = new LjHuff?[4];
        // jdmarker.c reset_marker_reader: arithmetic conditioning defaults for all 16 tables.
        var dcL = new int[16];
        var dcU = new int[16];
        var acK = new int[16];
        Array.Fill(dcU, 1);
        Array.Fill(acK, 5);
        LjArith? ar = null;

        static InvalidDataException BadLength() => new("Bogus marker length");

        while (true)
        {
            while (pos < d.Length && d[pos] != 0xFF) pos++;
            while (pos < d.Length && d[pos] == 0xFF) pos++;
            if (pos >= d.Length) break;   // end of data: libjpeg's source manager supplies a fake EOI (a warning)
            int marker = d[pos++];
            if (marker == 0xD9) break;
            if (marker == 0) continue;   // a stuffed 0xFF00 outside entropy data: discarded (next_marker)
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue;
            if (marker == 0xD8) throw new InvalidDataException("Invalid JPEG file structure: two SOI markers");
            // Markers libjpeg does not know (DHP, EXP, JPGn, RESn): JERR_UNKNOWN_MARKER.
            bool known = marker is >= 0xC0 and <= 0xCF or 0xDA or 0xDB or 0xDC or 0xDD or >= 0xE0 and <= 0xEF or 0xFE;
            if (!known || marker is 0xC8)
            {
                if (marker is 0xC8) throw new NotSupportedException($"Unsupported JPEG process: SOF type 0x{marker:x2}");
                throw new InvalidDataException($"Unsupported marker type 0x{marker:x2}");
            }
            // A segment running past the data reads what libjpeg's source manager supplies there: a warning and fake
            // EOI bytes (FF D9 repeated), which the marker parsers then consume as fields.
            if (pos + 2 > d.Length) d = PadWithFakeEoi(d, pos + 2);
            int len = (d[pos] << 8) | d[pos + 1];
            int seg = pos + 2, end = pos + len;
            if (end > d.Length) d = PadWithFakeEoi(d, end);
            if (end < seg) end = seg;   // a length below 2 skips nothing (skip_variable)
            switch (marker)
            {
                case 0xC0: case 0xC1: case 0xC2: case 0xC3: case 0xC9: case 0xCA: case 0xCB:
                {
                    // jdmarker.c get_sof + jdinput.c initial_setup.
                    if (sawFrame) throw new InvalidDataException("Invalid JPEG file structure: two SOF markers");
                    if (len < 8) throw BadLength();
                    progressive = marker is 0xC2 or 0xCA;
                    arith = marker is 0xC9 or 0xCA or 0xCB;
                    lossless = marker is 0xC3 or 0xCB;
                    precision = d[seg];
                    height = (d[seg + 1] << 8) | d[seg + 2];
                    width = (d[seg + 3] << 8) | d[seg + 4];
                    nComp = d[seg + 5];
                    if (width == 0 || height == 0 || nComp == 0) throw new InvalidDataException("Empty JPEG image (DNL not supported)");
                    if (len - 8 != nComp * 3) throw BadLength();
                    if (width > 65500 || height > 65500) throw new InvalidDataException("Maximum supported image dimension is 65500 pixels");
                    if (lossless ? precision is < 2 or > 16 : precision is not (8 or 12))
                        throw new InvalidDataException($"Unsupported JPEG data precision {precision}");
                    if (nComp > 10) throw new InvalidDataException($"Too many color components: {nComp}, max 10");
                    if (marker == 0xCB) throw new NotSupportedException("Sorry, arithmetic coding is not implemented (lossless)");
                    if ((long)width * height > maxPixels)
                        throw new InvalidDataException($"JPEG image {width}x{height} exceeds the limit of {maxPixels} pixels.");
                    comps = new LjComp[nComp];
                    for (int i = 0; i < nComp; i++)
                    {
                        int o = seg + 6 + i * 3;
                        comps[i] = new LjComp { Id = d[o], H = d[o + 1] >> 4, V = d[o + 1] & 15, Tq = d[o + 2] };
                        if (comps[i].H is < 1 or > 4 || comps[i].V is < 1 or > 4) throw new InvalidDataException("Bogus sampling factors");
                        maxH = Math.Max(maxH, comps[i].H);
                        maxV = Math.Max(maxV, comps[i].V);
                    }
                    // Beyond libjpeg-turbo's output: 2 or 5..10 components have no colour space we can return.
                    if (nComp is 2 or > 4) throw new NotSupportedException($"JPEG files with {nComp} components are not supported.");
                    sawFrame = true;
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
                case 0xC5: case 0xC6: case 0xC7: case 0xCD: case 0xCE: case 0xCF:
                    throw new NotSupportedException($"Unsupported JPEG process: SOF type 0x{marker:x2}");
                case 0xC4:
                {
                    // jdmarker.c get_dht; the tables are validated when a scan uses them (jpeg_make_d_derived_tbl).
                    long length = len - 2;
                    int p = seg;
                    while (length > 16)
                    {
                        int index = d[p];
                        var counts = d.AsSpan(p + 1, 16);
                        int total = 0;
                        foreach (byte b in counts) total += b;
                        length -= 17;
                        if (total > 256 || total > length) throw new InvalidDataException("Bogus Huffman table definition");
                        bool ac = (index & 0x10) != 0;
                        int th = ac ? index - 0x10 : index;
                        if (th is < 0 or > 3) throw new InvalidDataException($"Bogus DHT index {th}");
                        var h = new LjHuff(counts, d.AsSpan(p + 17, total));
                        if (ac) acHuff[th] = h; else dcHuff[th] = h;
                        p += 17 + total;
                        length -= total;
                    }
                    if (length != 0) throw BadLength();
                    break;
                }
                case 0xCC:
                {
                    // jdmarker.c get_dac: 16 DC and 16 AC conditioning tables.
                    long length = len - 2;
                    int p = seg;
                    while (length > 0)
                    {
                        if (p + 1 >= end) throw BadLength();
                        int index = d[p], v = d[p + 1];
                        p += 2;
                        length -= 2;
                        if (index >= 32) throw new InvalidDataException($"Bogus DAC index {index}");
                        if (index >= 16) acK[index - 16] = v;
                        else
                        {
                            dcL[index] = v & 15;
                            dcU[index] = v >> 4;
                            if (dcL[index] > dcU[index]) throw new InvalidDataException($"Bogus DAC value 0x{v:x}");
                        }
                    }
                    if (length != 0) throw BadLength();
                    break;
                }
                case 0xDB:
                {
                    // jdmarker.c get_dqt.
                    long length = len - 2;
                    int p = seg;
                    while (length > 0)
                    {
                        int pq = d[p] >> 4, tq = d[p] & 15;
                        if (tq > 3) throw new InvalidDataException($"Bogus DQT index {tq}");
                        int size = 1 + (pq != 0 ? 128 : 64);
                        if (p + size > end) throw BadLength();
                        var t = new int[64];
                        for (int i = 0; i < 64; i++)
                            t[LjNatural[i]] = pq != 0 ? (d[p + 1 + 2 * i] << 8) | d[p + 2 + 2 * i] : d[p + 1 + i];
                        qt[tq] = t;
                        p += size;
                        length -= size;
                    }
                    if (length != 0) throw BadLength();
                    break;
                }
                case 0xDD:
                    if (len != 4) throw BadLength();
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
                    // jdmarker.c get_sos.
                    if (!sawFrame) throw new InvalidDataException("Invalid JPEG file structure: SOS before SOF");
                    if (len < 3) throw BadLength();
                    int ns = d[seg];
                    if (len != ns * 2 + 6 || ns is < 1 or > 4) throw BadLength();
                    var scomps = new LjComp[ns];
                    for (int i = 0; i < ns; i++)
                    {
                        int cid = d[seg + 1 + i * 2], tbl = d[seg + 2 + i * 2];
                        LjComp? c = null;
                        for (int ci = 0; ci < comps.Length && ci < 4; ci++)
                            if (comps[ci].Id == cid && Array.IndexOf(scomps, comps[ci], 0, i) < 0) { c = comps[ci]; break; }
                        c = c ?? throw new InvalidDataException($"Invalid component ID {cid} in SOS");
                        c.DcTbl = tbl >> 4;
                        c.AcTbl = tbl & 15;
                        scomps[i] = c;
                    }
                    int so = seg + 1 + ns * 2;
                    int ss = d[so], se = d[so + 1], ah = d[so + 2] >> 4, al = d[so + 2] & 15;

                    // jdinput.c consume_markers: a sequential first scan holding every component makes a single-scan
                    // file, and a second SOS is then an error (JERR_EOI_EXPECTED).
                    if (scans == 0) multiScan = progressive || ns < nComp;
                    else if (!multiScan) throw new InvalidDataException("Didn't expect more than one scan");
                    // jdinput.c per_scan_setup: an interleaved MCU holds at most 10 blocks.
                    if (ns > 1 && scomps.Sum(c => c.H * c.V) > 10) throw new InvalidDataException("Sampling factors too large for interleaved scan");
                    // jdmaster / jdhuff jinit_huff_decoder at jpeg_start_decompress (first SOS): Motion-JPEG default
                    // tables for the Huffman slots 0 and 1 still undefined (sequential Huffman only).
                    if (!entropyStarted)
                    {
                        entropyStarted = true;
                        if (!arith && !progressive && !lossless) InstallStdHuffTables(dcHuff, acHuff);
                    }
                    ValidateLjScan(scomps, lossless, progressive, arith, precision, ss, se, ah, al, dcHuff, acHuff);
                    // jdphuff / jdarith start_pass: progression status per coefficient (input_scan_number = scans + 1).
                    if (progressive)
                        foreach (var c in scomps)
                        {
                            for (int k = Math.Min(ss, 1); k <= Math.Max(se, 9); k++) c.PrevBits[k] = scans > 0 ? c.CurBits[k] : 0;
                            for (int k = ss; k <= se; k++) c.CurBits[k] = al;
                        }
                    // jdinput.c latch_quant_tables: each component's table as it was at its first scan.
                    if (!lossless)
                        foreach (var c in scomps)
                            c.Qt ??= c.Tq <= 3 && qt[c.Tq] is { } q ? (int[])q.Clone()
                                : throw new InvalidDataException($"Quantization table 0x{c.Tq:x2} was not defined");

                    var bits = new LjBits(d, end);
                    if (lossless)
                        DecodeLosslessScan(bits, scomps, dcHuff, width, height, maxH, maxV, restartInterval, ss, al, precision);
                    else if (arith)
                        DecodeArithScan(bits, ar ??= new LjArith(), scomps, width, height, maxH, maxV, restartInterval,
                            progressive, ss, se, ah, al, dcL, dcU, acK);
                    else
                        DecodeHuffScan(bits, scomps, dcHuff, acHuff, width, height, maxH, maxV, restartInterval,
                            progressive, ss, se, ah, al);
                    scans++;
                    lastGoodRow = bits.LastGoodRow;
                    pos = bits.EndOfSegment();
                    continue;
                }
            }
            pos = end;
        }
        if (!sawFrame || scans == 0) throw new InvalidDataException("JPEG datastream contains no image");

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
            default: throw new NotSupportedException($"JPEG files with {nComp} components are not supported.");
        }
        // libjpeg: no lossy colour conversion in lossless mode (JERR_CONVERSION_NOTIMPL).
        if (lossless && space is 1 or 4) throw new NotSupportedException("Unsupported color conversion request");

        // Component planes at their downsampled size.
        int max = lossless ? (1 << precision) - 1 : precision == 12 ? 4095 : 255;
        var planes = new int[nComp][];
        bool smooth = progressive && LjSmoothingOk(comps);
        int totalImcuRows = (height + maxV * 8 - 1) / (maxV * 8);
        for (int ci = 0; ci < nComp; ci++)
        {
            var c = comps[ci];
            if (lossless) { planes[ci] = c.Samples; continue; }
            // A component that never appeared in a scan has no table (libjpeg leaves its multipliers zero).
            var t = c.Qt ?? new int[64];
            var plane = new int[c.DownW * c.DownH];
            Span<int> blk = stackalloc int[64];
            var smoothRow = smooth ? new short[c.WidthInBlocks * 64] : null;
            for (int by = 0; by < c.HeightInBlocks; by++)
            {
                if (smoothRow != null) LjSmoothRow(c, by, totalImcuRows, lastGoodRow, scans > 1, smoothRow);
                for (int bx = 0; bx < c.WidthInBlocks; bx++)
                {
                    var src = smoothRow != null ? smoothRow.AsSpan(bx * 64, 64) : c.Coef.AsSpan((by * c.AllocW + bx) * 64, 64);
                    LjIdctIslow(src, t, blk, precision);
                    for (int y = 0; y < 8 && by * 8 + y < c.DownH; y++)
                        for (int x = 0; x < 8 && bx * 8 + x < c.DownW; x++)
                            plane[(by * 8 + y) * c.DownW + bx * 8 + x] = blk[y * 8 + x];
                }
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

        // Corrupt lossless data can undifference past 2^P - 1 (libjpeg outputs such samples as they are); a frame
        // sample cannot exceed the maximum, so it is clamped.
        static ushort Scale16(int v, int max) => (ushort)(((long)Math.Clamp(v, 0, max) * 65535 + max / 2) / max);
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

    // jdhuff.c jinit_huff_decoder std_huff_tables: Motion-JPEG streams omit the default (Annex K.3) tables.
    private static void InstallStdHuffTables(LjHuff?[] dc, LjHuff?[] ac)
    {
        dc[0] ??= new LjHuff(JpegTables.DcLuminanceBits, JpegTables.DcLuminanceValues);
        ac[0] ??= new LjHuff(JpegTables.AcLuminanceBits, JpegTables.AcLuminanceValues);
        dc[1] ??= new LjHuff(JpegTables.DcChrominanceBits, JpegTables.DcChrominanceValues);
        ac[1] ??= new LjHuff(JpegTables.AcChrominanceBits, JpegTables.AcChrominanceValues);
    }

    // The checks libjpeg-turbo makes when a scan starts (jdphuff / jdarith / jdlossls start_pass, then
    // jpeg_make_d_derived_tbl for every table the scan uses). Sequential scans with odd Ss/Se/Ah/Al only warn.
    private static void ValidateLjScan(LjComp[] sc, bool lossless, bool progressive, bool arith, int precision,
        int ss, int se, int ah, int al, LjHuff?[] dcH, LjHuff?[] acH)
    {
        InvalidDataException BadScan() => new($"Invalid progressive/lossless parameters Ss={ss} Se={se} Ah={ah} Al={al}");
        if (lossless)
        {
            if (ss is < 1 or > 7 || se != 0 || ah != 0 || al >= precision) throw BadScan();
            foreach (var c in sc) CheckHuff(dcH, c.DcTbl, dc: true, lossless: true);
            return;
        }
        if (progressive)
        {
            bool bad = ss == 0 ? se != 0 : se < ss || se > 63 || sc.Length != 1;
            if (ah != 0 && al != ah - 1) bad = true;
            if (al > 13) bad = true;
            if (bad) throw BadScan();
        }
        foreach (var c in sc)
        {
            bool needDc = !progressive || (ss == 0 && ah == 0), needAc = !progressive || ss != 0;
            if (arith)
            {
                if (needDc && c.DcTbl > 15) throw new InvalidDataException($"Arithmetic table 0x{c.DcTbl:x2} was not defined");
                if (needAc && c.AcTbl > 15) throw new InvalidDataException($"Arithmetic table 0x{c.AcTbl:x2} was not defined");
                continue;
            }
            if (needDc) CheckHuff(dcH, c.DcTbl, dc: true, lossless: false);
            if (needAc) CheckHuff(acH, c.AcTbl, dc: false, lossless: false);
        }
    }

    // jdhuff.c jpeg_make_d_derived_tbl: the table exists, its code lengths form a valid prefix code, DC symbols fit.
    private static void CheckHuff(LjHuff?[] tables, int tbl, bool dc, bool lossless)
    {
        if (tbl > 3 || tables[tbl] is not { } h) throw new InvalidDataException($"Huffman table 0x{tbl:x2} was not defined");
        if (h.Bad || (dc && h.MaxSymbol > (lossless ? 16 : 15))) throw new InvalidDataException("Bogus Huffman table definition");
    }

    // ── Block smoothing (jdcoefct.c smoothing_ok / decompress_smooth_data) ───────────────────────────────────────
    // A progressive image whose low AC coefficients are not all known yet (a truncated or partial file) is output with
    // those coefficients estimated from the 5x5 neighbourhood of DC values, as libjpeg-turbo does by default
    // (do_block_smoothing). Natural positions of the first 9 zigzag AC coefficients: 01 10 20 11 02 03 12 21 30.
    private static readonly int[] SmoothPos = [0, 1, 8, 16, 9, 2, 3, 10, 17, 24];

    private static bool LjSmoothingOk(LjComp[] comps)
    {
        bool useful = false;
        foreach (var c in comps)
        {
            if (c.Qt is not { } q) return false;
            foreach (int p in SmoothPos) if (q[p] == 0) return false;
            if (c.CurBits[0] < 0) return false;
            for (int k = 1; k < 10; k++) if (c.CurBits[k] != 0) useful = true;
        }
        return useful;
    }

    // One component's block row, smoothed into ws (WidthInBlocks blocks of 64), for the IDCT.
    private static void LjSmoothRow(LjComp c, int by, int totalImcuRows, int lastGoodRow, bool prevLatch, short[] ws)
    {
        int v = c.V, r = by / v, blockRow = by % v, lastRow = totalImcuRows - 1;
        int blockRows = r < lastRow ? v : (c.HeightInBlocks % v == 0 ? v : c.HeightInBlocks % v);
        int imageBlockRows = blockRows * totalImcuRows, imageBlockRow = r * blockRows + blockRow;
        int row = by;
        int prevRow = imageBlockRow > 0 ? row - 1 : row;
        int prevPrevRow = imageBlockRow > 1 ? row - 2 : prevRow;
        int nextRow = imageBlockRow < imageBlockRows - 1 ? row + 1 : row;
        int nextNextRow = imageBlockRow < imageBlockRows - 2 ? row + 2 : nextRow;
        // If the current scan is incomplete, rows past the last good one use the bits as of the previous scan.
        int[] bits = r > lastGoodRow ? (prevLatch ? c.PrevBits : c.NoBits) : c.CurBits;
        bool changeDc = true;
        for (int k = 1; k < 10; k++) changeDc &= bits[k] == -1;
        var q = c.Qt!;
        long q00 = q[0], q01 = q[1], q10 = q[8], q20 = q[16], q11 = q[9], q02 = q[2];
        long q03 = changeDc ? q[3] : 0, q12 = changeDc ? q[10] : 0, q21 = changeDc ? q[17] : 0, q30 = changeDc ? q[24] : 0;
        short[] coef = c.Coef;
        int aw = c.AllocW;
        int Dc(int rr, int col) => coef[(rr * aw + col) * 64];

        int dc01, dc02, dc03, dc04, dc05, dc06, dc07, dc08, dc09, dc10, dc11, dc12, dc13, dc14, dc15;
        int dc16, dc17, dc18, dc19, dc20, dc21, dc22, dc23, dc24, dc25;
        dc01 = dc02 = dc03 = dc04 = dc05 = Dc(prevPrevRow, 0);
        dc06 = dc07 = dc08 = dc09 = dc10 = Dc(prevRow, 0);
        dc11 = dc12 = dc13 = dc14 = dc15 = Dc(row, 0);
        dc16 = dc17 = dc18 = dc19 = dc20 = Dc(nextRow, 0);
        dc21 = dc22 = dc23 = dc24 = dc25 = Dc(nextNextRow, 0);
        int lastCol = c.WidthInBlocks - 1;

        static short Pred(long num, long qq, int al)
        {
            int pred;
            if (num >= 0)
            {
                pred = (int)(((qq << 7) + num) / (qq << 8));
                if (al > 0 && pred >= (1 << al)) pred = (1 << al) - 1;
            }
            else
            {
                pred = (int)(((qq << 7) - num) / (qq << 8));
                if (al > 0 && pred >= (1 << al)) pred = (1 << al) - 1;
                pred = -pred;
            }
            return (short)pred;
        }

        for (int b = 0; b <= lastCol; b++)
        {
            var w = ws.AsSpan(b * 64, 64);
            coef.AsSpan((row * aw + b) * 64, 64).CopyTo(w);
            if (b == 0 && b < lastCol)
            {
                dc04 = dc05 = Dc(prevPrevRow, 1);
                dc09 = dc10 = Dc(prevRow, 1);
                dc14 = dc15 = Dc(row, 1);
                dc19 = dc20 = Dc(nextRow, 1);
                dc24 = dc25 = Dc(nextNextRow, 1);
            }
            if (b + 1 < lastCol)
            {
                dc05 = Dc(prevPrevRow, b + 2);
                dc10 = Dc(prevRow, b + 2);
                dc15 = Dc(row, b + 2);
                dc20 = Dc(nextRow, b + 2);
                dc25 = Dc(nextNextRow, b + 2);
            }
            int al;
            if ((al = bits[1]) != 0 && w[1] == 0)
                w[1] = Pred(q00 * (changeDc
                    ? -dc01 - dc02 + dc04 + dc05 - 3 * dc06 + 13 * dc07 - 13 * dc09 + 3 * dc10 - 3 * dc11 + 38 * dc12 - 38 * dc14
                      + 3 * dc15 - 3 * dc16 + 13 * dc17 - 13 * dc19 + 3 * dc20 - dc21 - dc22 + dc24 + dc25
                    : -7 * dc11 + 50 * dc12 - 50 * dc14 + 7 * dc15), q01, al);
            if ((al = bits[2]) != 0 && w[8] == 0)
                w[8] = Pred(q00 * (changeDc
                    ? -dc01 - 3 * dc02 - 3 * dc03 - 3 * dc04 - dc05 - dc06 + 13 * dc07 + 38 * dc08 + 13 * dc09 - dc10 + dc16
                      - 13 * dc17 - 38 * dc18 - 13 * dc19 + dc20 + dc21 + 3 * dc22 + 3 * dc23 + 3 * dc24 + dc25
                    : -7 * dc03 + 50 * dc08 - 50 * dc18 + 7 * dc23), q10, al);
            if ((al = bits[3]) != 0 && w[16] == 0)
                w[16] = Pred(q00 * (changeDc
                    ? dc03 + 2 * dc07 + 7 * dc08 + 2 * dc09 - 5 * dc12 - 14 * dc13 - 5 * dc14 + 2 * dc17 + 7 * dc18 + 2 * dc19 + dc23
                    : -dc03 + 13 * dc08 - 24 * dc13 + 13 * dc18 - dc23), q20, al);
            if ((al = bits[4]) != 0 && w[9] == 0)
                w[9] = Pred(q00 * (changeDc
                    ? -dc01 + dc05 + 9 * dc07 - 9 * dc09 - 9 * dc17 + 9 * dc19 + dc21 - dc25
                    : dc10 + dc16 - 10 * dc17 + 10 * dc19 - dc02 - dc20 + dc22 - dc24 + dc04 - dc06 + 10 * dc07 - 10 * dc09), q11, al);
            if ((al = bits[5]) != 0 && w[2] == 0)
                w[2] = Pred(q00 * (changeDc
                    ? 2 * dc07 - 5 * dc08 + 2 * dc09 + dc11 + 7 * dc12 - 14 * dc13 + 7 * dc14 + dc15 + 2 * dc17 - 5 * dc18 + 2 * dc19
                    : -dc11 + 13 * dc12 - 24 * dc13 + 13 * dc14 - dc15), q02, al);
            if (changeDc)
            {
                if ((al = bits[6]) != 0 && w[3] == 0) w[3] = Pred(q00 * (dc07 - dc09 + 2 * dc12 - 2 * dc14 + dc17 - dc19), q03, al);
                if ((al = bits[7]) != 0 && w[10] == 0) w[10] = Pred(q00 * (dc07 - 3 * dc08 + dc09 - dc17 + 3 * dc18 - dc19), q12, al);
                if ((al = bits[8]) != 0 && w[17] == 0) w[17] = Pred(q00 * (dc07 - dc09 - 3 * dc12 + 3 * dc14 + dc17 - dc19), q21, al);
                if ((al = bits[9]) != 0 && w[24] == 0) w[24] = Pred(q00 * (dc07 + 2 * dc08 + dc09 - dc17 - 2 * dc18 - dc19), q30, al);
                long num = q00 * (-2 * dc01 - 6 * dc02 - 8 * dc03 - 6 * dc04 - 2 * dc05 - 6 * dc06 + 6 * dc07 + 42 * dc08 + 6 * dc09
                    - 6 * dc10 - 8 * dc11 + 42 * dc12 + 152 * dc13 + 42 * dc14 - 8 * dc15 - 6 * dc16 + 6 * dc17 + 42 * dc18
                    + 6 * dc19 - 6 * dc20 - 2 * dc21 - 6 * dc22 - 8 * dc23 - 6 * dc24 - 2 * dc25);
                w[0] = Pred(num, q00, 0);
            }
            dc01 = dc02; dc02 = dc03; dc03 = dc04; dc04 = dc05;
            dc06 = dc07; dc07 = dc08; dc08 = dc09; dc09 = dc10;
            dc11 = dc12; dc12 = dc13; dc13 = dc14; dc14 = dc15;
            dc16 = dc17; dc17 = dc18; dc18 = dc19; dc19 = dc20;
            dc21 = dc22; dc22 = dc23; dc23 = dc24; dc24 = dc25;
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
        bool skipMcu = false, mcuStart = false, goodAtStart = false;
        bool dcRefine = progressive && ss == 0 && ah != 0;
        foreach (var (c, blk) in LjBlocks(sc, width, height, maxH, maxV, () =>
        {
            mcuStart = true;
            goodAtStart = !b.Insufficient;   // jdcoefct consume_data checks before decode_mcu (and its restart)
            if (restartInterval != 0)
            {
                if (restartsToGo == 0)
                {
                    b.Restart();
                    foreach (var cc in sc) cc.LastDc = 0;
                    eobrun = 0;
                    restartsToGo = restartInterval;
                }
                restartsToGo--;
            }
            skipMcu = b.Insufficient && !dcRefine;
        }))
        {
            if (mcuStart)
            {
                mcuStart = false;
                if (goodAtStart) b.LastGoodRow = blk / c.AllocW / c.V;
            }
            if (skipMcu) continue;
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
        bool error = false, mcuStart = false;
        foreach (var (c, blk) in LjBlocks(sc, width, height, maxH, maxV, () =>
        {
            mcuStart = true;
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
            if (mcuStart) { mcuStart = false; b.LastGoodRow = blk / c.AllocW / c.V; }
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
            // jdlhuff.c decode_mcus: once the data ran out, an MCU row's differences are zero and the undifferencer
            // restarts (its first-row prediction), so the rest of the segment comes out at the centre value.
            if (b.Insufficient)
            {
                restartAt[my] = true;
                foreach (var c in sc)
                {
                    int vv = single ? 1 : c.V;
                    Array.Clear(c.Diff, my * vv * c.AllocW, Math.Min(vv * c.AllocW, c.Diff.Length - my * vv * c.AllocW));
                }
                continue;
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
