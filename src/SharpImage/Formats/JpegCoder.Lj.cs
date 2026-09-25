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
    // libjpeg's warnings (WARNMS): recovered from, unless the decode is strict (djpeg -strict makes every warning fatal).
    [ThreadStatic] private static bool t_ljStrict;
    // jdmarker.c marker->discarded_bytes: bytes skipped before a marker (and whole unread bytes of the bit buffer at a
    // restart), reported by the next next_marker call; it persists across scans.
    [ThreadStatic] private static int t_ljDiscarded;

    private static void LjWarn(string message)
    {
        if (t_ljStrict) throw new InvalidDataException(message);
    }

    // The entropy-coded data as libjpeg-turbo reads it (jdhuff.h / jdhuff.c, 64-bit bit buffer): Pos is the source
    // position (next_input_byte), Bits the bit buffer's bits_left, filled as jpeg_fill_bit_buffer does (to
    // MIN_GET_BITS = 57, zeros past a marker when bits are needed) or, for sequential MCUs decoded on the fast path,
    // six bytes at a time (FILL_BIT_BUFFER_FAST). Tracking libjpeg's read position exactly matters for the bytes it
    // reports as discarded before the next marker.
    // entropy->insufficient_data: set once a decoder needs bits past the real data, after which the Huffman decoders
    // leave MCUs untouched until a restart marker resynchronises.
    private sealed class LjBits
    {
        private const int MinGetBits = 57;
        private readonly byte[] d;
        private readonly int realLength;
        public int Pos;
        public int UnreadMarker;
        public bool Insufficient;
        public int LastGoodRow;   // cinfo->master->last_good_iMCU_row: iMCU row of the last MCU started with data left
        public bool Fast;          // decode_mcu_fast in progress
        public bool FastMarker;    // ... and it met a marker (the MCU is redone on the slow path)
        private int nextRestartNum;
        private ulong buf;
        private int bits;

        public LjBits(byte[] data, int pos, int realLength) { d = data; Pos = pos; this.realLength = realLength; }

        public (int Pos, ulong Buf, int Bits, int Marker, bool Insufficient) Save() => (Pos, buf, bits, UnreadMarker, Insufficient);

        public void Load((int Pos, ulong Buf, int Bits, int Marker, bool Insufficient) s)
        {
            (Pos, buf, bits, UnreadMarker, Insufficient) = s;
            Fast = FastMarker = false;
        }

        // jdatasrc.c (djpeg's stdio source): the file is read in 4096-byte chunks, so bytes_in_buffer is what is left of
        // the current chunk (0 right at a chunk boundary, until the next read).
        public long BytesInBuffer => Pos % 4096 == 0 ? 0 : Math.Min((Pos / 4096 + 1) * 4096L, realLength) - Pos;

        // Next data byte (0xFF00 -> 0xFF), or -1 at a marker (left unread; the end of the data reads as EOI, as
        // libjpeg's source manager inserts one).
        public int NextByte()
        {
            if (UnreadMarker != 0) return -1;
            if (Pos >= d.Length) { LjWarn("Premature end of JPEG file"); UnreadMarker = 0xD9; return -1; }
            int b = d[Pos];
            if (b != 0xFF) { Pos++; return b; }
            int q = Pos + 1;
            while (q < d.Length && d[q] == 0xFF) q++;
            if (q < d.Length && d[q] == 0) { Pos = q + 1; return 0xFF; }
            if (q >= d.Length) LjWarn("Premature end of JPEG file");
            UnreadMarker = q < d.Length ? d[q] : 0xD9;
            Pos = q + 1;
            return -1;
        }

        // jpeg_fill_bit_buffer(nbits): read to MIN_GET_BITS unless a marker is met; then, if nbits are still missing, the
        // data has run out (JWRN_HIT_MARKER once) and zeros fill the buffer to MIN_GET_BITS.
        private void Fill(int nbits)
        {
            while (bits < MinGetBits && UnreadMarker == 0)
            {
                int b = NextByte();
                if (b < 0) break;
                buf = (buf << 8) | (uint)b;
                bits += 8;
            }
            if (UnreadMarker != 0 && nbits > bits)
            {
                if (!Insufficient) LjWarn("Corrupt JPEG data: premature end of data segment");
                Insufficient = true;
                buf <<= MinGetBits - bits;
                bits = MinGetBits;
            }
        }

        // FILL_BIT_BUFFER_FAST (64-bit): six GET_BYTEs when 16 bits or fewer are left; a marker (or FF FF) backs out and
        // reads as zero bytes, and fails the fast MCU.
        private void FillFast()
        {
            if (bits > 16) return;
            for (int i = 0; i < 6; i++)
            {
                int c0 = FastMarker ? 0 : d[Pos];
                if (!FastMarker && c0 == 0xFF)
                {
                    if (d[Pos + 1] == 0) Pos += 2;
                    else { FastMarker = true; c0 = 0; }
                }
                else if (!FastMarker) Pos++;
                buf = (buf << 8) | (uint)c0;
                bits += 8;
            }
        }

        // CHECK_BIT_BUFFER + GET_BITS (on the fast path, FILL_BIT_BUFFER_FAST + GET_BITS).
        public int GetBits(int n)
        {
            if (n == 0) return 0;
            if (Fast) FillFast();
            else if (bits < n) Fill(n);
            bits -= n;
            return (int)((buf >> bits) & ((1UL << n) - 1));
        }

        private int Raw(int n)
        {
            bits -= n;
            return (int)((buf >> bits) & ((1UL << n) - 1));
        }

        // HUFF_DECODE (jdhuff.h) / HUFF_DECODE_FAST.
        public int Decode(LjHuff h)
        {
            if (Fast)
            {
                FillFast();
                int lk = h.Look[(int)((buf >> (bits - 8)) & 0xFF)];
                if (lk != 0) { bits -= lk >> 8; return lk & 0xFF; }
                int nb = 9, cd = Raw(9);
                while (cd > h.MaxCode[nb]) { cd = (cd << 1) | Raw(1); nb++; }
                // the fast path decodes a corrupt code as 0 without a warning
                return nb > 16 ? 0 : h.Vals[(cd + h.ValOffset[nb]) & 0xFF];
            }
            if (bits < 8)
            {
                Fill(0);
                if (bits < 8) return SlowDecode(h, 1);
            }
            int look = h.Look[(int)((buf >> (bits - 8)) & 0xFF)];
            if (look != 0) { bits -= look >> 8; return look & 0xFF; }
            return SlowDecode(h, 9);
        }

        // jpeg_huff_decode: MaxCode[17] is a sentinel, so a corrupt code consumes 17 bits, then decodes as 0.
        private int SlowDecode(LjHuff h, int l)
        {
            int code = GetBits(l);
            while (code > h.MaxCode[l]) { code = (code << 1) | GetBits(1); l++; }
            if (l > 16) { LjWarn("Corrupt JPEG data: bad Huffman code"); return 0; }
            return h.Vals[(code + h.ValOffset[l]) & 0xFF];
        }

        // process_restart + jdmarker.c read_restart_marker: whole unread bytes of the bit buffer count as discarded, the
        // buffer is emptied, then the RSTn marker is read (next_marker if none is pending); a wrong or missing marker
        // resynchronises as jpeg_resync_to_restart does. The data-exhausted state clears unless the resync left us at a
        // marker (the next segment then counts as empty).
        public void Restart()
        {
            t_ljDiscarded += bits / 8;
            bits = 0;
            buf = 0;
            if (UnreadMarker == 0) NextMarker();
            if (UnreadMarker == 0xD0 + nextRestartNum) UnreadMarker = 0;
            else Resync(nextRestartNum);
            nextRestartNum = (nextRestartNum + 1) & 7;
            if (UnreadMarker == 0) Insufficient = false;
        }

        // jdmarker.c next_marker: skip anything up to 0xFF, the 0xFF fill bytes, and stuffed 0xFF00 pairs (counted as
        // discarded, JWRN_EXTRANEOUS_DATA).
        private void NextMarker()
        {
            for (;;)
            {
                while (Pos < d.Length && d[Pos] != 0xFF) { Pos++; t_ljDiscarded++; }
                while (Pos < d.Length && d[Pos] == 0xFF) Pos++;
                if (Pos >= d.Length) { LjWarn("Premature end of JPEG file"); UnreadMarker = 0xD9; return; }
                int c = d[Pos++];
                if (c != 0)
                {
                    if (t_ljDiscarded != 0)
                    {
                        int n = t_ljDiscarded;
                        t_ljDiscarded = 0;
                        LjWarn($"Corrupt JPEG data: {n} extraneous bytes before marker 0x{c:x2}");
                    }
                    UnreadMarker = c;
                    return;
                }
                t_ljDiscarded += 2;
            }
        }

        // jdmarker.c jpeg_resync_to_restart.
        private void Resync(int desired)
        {
            LjWarn($"Corrupt JPEG data: found marker 0x{UnreadMarker:x2} instead of RST{desired}");
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

        // Where the marker reader resumes after the scan: at the pending marker (its 0xFF), or where the entropy decoder
        // stopped reading (next_marker then counts what it skips).
        public (int Pos, bool MarkerPending) EndOfScan() => UnreadMarker != 0 ? (Pos - 2, true) : (Pos, false);
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
    internal static ImageFrame ReadLibjpegExact(byte[] d, long maxPixels = DefaultMaxPixels) =>
        DecodeLj(d, new JpegDecodeOptions { MaxPixels = maxPixels });

    internal static ImageFrame ReadLibjpegExact(byte[] d, JpegDecodeOptions options) => DecodeLj(d, options);

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

    private static ImageFrame DecodeLj(byte[] d, JpegDecodeOptions opts)
    {
        bool wasStrict = t_ljStrict;
        t_ljStrict = opts.Strict;
        try { return DecodeLjCore(d, opts); }
        finally { t_ljStrict = wasStrict; }
    }

    private static ImageFrame DecodeLjCore(byte[] d, JpegDecodeOptions opts)
    {
        long maxPixels = opts.MaxPixels;
        int realLength = d.Length;
        t_ljDiscarded = 0;
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

        bool markerPending = false;
        while (true)
        {
            while (pos < d.Length && d[pos] != 0xFF) { pos++; t_ljDiscarded++; }
            while (pos < d.Length && d[pos] == 0xFF) pos++;
            if (pos >= d.Length)
            {
                // end of data: libjpeg's source manager supplies a fake EOI (a warning)
                LjWarn("Premature end of JPEG file");
                break;
            }
            int marker = d[pos++];
            if (marker == 0) { t_ljDiscarded += 2; continue; }   // a stuffed 0xFF00 outside entropy data: discarded (next_marker)
            if (!markerPending && t_ljDiscarded != 0)
            {
                int n = t_ljDiscarded;
                t_ljDiscarded = 0;
                LjWarn($"Corrupt JPEG data: {n} extraneous bytes before marker 0x{marker:x2}");
            }
            markerPending = false;
            if (marker == 0xD9) break;
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
            if (pos + 2 > d.Length) { LjWarn("Premature end of JPEG file"); d = PadWithFakeEoi(d, pos + 2); }
            int len = (d[pos] << 8) | d[pos + 1];
            int seg = pos + 2, end = pos + len;
            if (end > d.Length) { LjWarn("Premature end of JPEG file"); d = PadWithFakeEoi(d, end); }
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
                    if (len >= 16 && d.AsSpan(seg, 5).SequenceEqual("JFIF\0"u8))
                    {
                        sawJfif = true;
                        if (d[seg + 5] != 1) LjWarn($"Warning: unknown JFIF revision number {d[seg + 5]}.{d[seg + 6]:D2}");
                    }
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
                    if (opts.MaxScans > 0 && scans + 1 > opts.MaxScans)
                        throw new InvalidDataException($"Scan number {scans + 1} exceeds maximum scans ({opts.MaxScans})");
                    ValidateLjScan(scomps, lossless, progressive, arith, precision, ss, se, ah, al, dcHuff, acHuff);
                    // jdphuff / jdarith start_pass: progression status per coefficient (input_scan_number = scans + 1).
                    if (progressive)
                        foreach (var c in scomps)
                        {
                            int cindex = Array.IndexOf(comps, c);
                            // AC without a prior DC scan, or a refinement of bits that were not sent: JWRN_BOGUS_PROGRESSION
                            if (ss != 0 && c.CurBits[0] < 0) LjWarn($"Inconsistent progression sequence for component {cindex} coefficient 0");
                            for (int k = Math.Min(ss, 1); k <= Math.Max(se, 9); k++) c.PrevBits[k] = scans > 0 ? c.CurBits[k] : 0;
                            for (int k = ss; k <= se; k++)
                            {
                                if (ah != Math.Max(c.CurBits[k], 0)) LjWarn($"Inconsistent progression sequence for component {cindex} coefficient {k}");
                                c.CurBits[k] = al;
                            }
                        }
                    // jdinput.c latch_quant_tables: each component's table as it was at its first scan.
                    if (!lossless)
                        foreach (var c in scomps)
                            c.Qt ??= c.Tq <= 3 && qt[c.Tq] is { } q ? (int[])q.Clone()
                                : throw new InvalidDataException($"Quantization table 0x{c.Tq:x2} was not defined");

                    var bits = new LjBits(d, end, realLength);
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
                    (pos, markerPending) = bits.EndOfScan();
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
                else if (sawAdobe)
                {
                    if (adobeTransform is not (0 or 1)) LjWarn($"Unknown Adobe color transform code {adobeTransform}");
                    space = adobeTransform == 0 ? 2 : 1;
                }
                else if (comps[0].Id == 1 && comps[1].Id == 2 && comps[2].Id == 3) space = lossless ? 2 : 1;
                else if (comps[0].Id == 82 && comps[1].Id == 71 && comps[2].Id == 66) space = 2;
                else space = lossless ? 2 : 1;
                break;
            case 4:
                if (sawAdobe && adobeTransform is not (0 or 2)) LjWarn($"Unknown Adobe color transform code {adobeTransform}");
                space = sawAdobe ? (adobeTransform == 0 ? 3 : 4) : 3;
                break;
            default: throw new NotSupportedException($"JPEG files with {nComp} components are not supported.");
        }
        // libjpeg: no lossy colour conversion in lossless mode (JERR_CONVERSION_NOTIMPL).
        if (lossless && space is 1 or 4) throw new NotSupportedException("Unsupported color conversion request");

        // Output dimensions and each component's IDCT size (jdmaster.c jpeg_core_output_dimensions /
        // jpeg_calc_output_dimensions): the smallest n/8 >= M/N, and larger IDCTs for subsampled components as far as
        // they replace upsampling.
        int max = lossless ? (1 << precision) - 1 : precision == 12 ? 4095 : 255;
        int minS = 8, outW = width, outH = height;
        if (!lossless)
        {
            long num = opts.ScaleNumerator, den = opts.ScaleDenominator;
            if (num <= 0 || den <= 0) throw new ArgumentOutOfRangeException(nameof(opts), "Scale factors must be positive");
            minS = 16;
            for (int n = 1; n <= 15; n++)
                if (num * 8 <= den * n) { minS = n; break; }
            outW = (int)(((long)width * minS + 7) / 8);
            outH = (int)(((long)height * minS + 7) / 8);
        }
        var ssize = new int[nComp];
        var dws = new int[nComp];
        var dhs = new int[nComp];
        for (int ci = 0; ci < nComp; ci++)
        {
            var c = comps[ci];
            int s = minS;
            if (!lossless)
                while (s < 8 && maxH * minS % (c.H * s * 2) == 0 && maxV * minS % (c.V * s * 2) == 0) s *= 2;
            ssize[ci] = s;
            dws[ci] = lossless ? c.DownW : (int)(((long)width * c.H * s + maxH * 8 - 1) / (maxH * 8));
            dhs[ci] = lossless ? c.DownH : (int)(((long)height * c.V * s + maxV * 8 - 1) / (maxV * 8));
        }

        // Output colour space (jdcolor.c): greyscale output takes Y of YCbCr / grey, or converts RGB.
        bool greyOut = opts.Grayscale && space != 0;
        if (greyOut && (space >= 3 || lossless)) throw new NotSupportedException("Unsupported color conversion request");
        var needed = new bool[nComp];
        for (int ci = 0; ci < nComp; ci++) needed[ci] = !greyOut || space == 2 || ci == 0;

        // Component planes at their (scaled) downsampled size.
        var planes = new int[nComp][];
        bool smooth = opts.BlockSmoothing && progressive && LjSmoothingOk(comps);
        int totalImcuRows = (height + maxV * 8 - 1) / (maxV * 8);
        int p1 = precision == 8 ? 2 : 1, center = (max + 1) / 2;
        int mask = 4 * (max + 1) - 1;
        int[]? rlPost = null, rlSimple = null;
        if (!lossless)
        {
            // jdmaster.c prepare_range_limit_table, indexed by (x & RANGE_MASK): IDCT_range_limit for the integer IDCTs,
            // sample_range_limit for the float one
            rlPost = new int[mask + 1];
            rlSimple = new int[mask + 1];
            for (int i = 0; i <= mask; i++)
            {
                int sg = i < 2 * (max + 1) ? i : i - 4 * (max + 1);
                rlPost[i] = Math.Clamp(sg + center, 0, max);
                rlSimple[i] = i <= max ? i : i < 2 * (max + 1) + center ? max : 0;
            }
        }
        ReadOnlySpan<short> aanScales =
        [
            16384, 22725, 21407, 19266, 16384, 12873, 8867, 4520, 22725, 31521, 29692, 26722, 22725, 17855, 12299, 6270,
            21407, 29692, 27969, 25172, 21407, 16819, 11585, 5906, 19266, 26722, 25172, 22654, 19266, 15137, 10426, 5315,
            16384, 22725, 21407, 19266, 16384, 12873, 8867, 4520, 12873, 17855, 16819, 15137, 12873, 10114, 6967, 3552,
            8867, 12299, 11585, 10426, 8867, 6967, 4799, 2446, 4520, 6270, 5906, 5315, 4520, 3552, 2446, 1247,
        ];
        ReadOnlySpan<double> aanFactors = [1.0, 1.387039845, 1.306562965, 1.175875602, 1.0, 0.785694958, 0.541196100, 0.275899379];
        for (int ci = 0; ci < nComp; ci++)
        {
            var c = comps[ci];
            if (lossless) { planes[ci] = c.Samples; continue; }
            if (!needed[ci]) continue;
            int s = ssize[ci], dw = dws[ci], dh = dhs[ci];
            var method = s == 8 ? opts.Dct : JpegDctMethod.IntegerSlow;
            // jddctmgr.c multiplier tables. A component that never appeared in a scan has no table (libjpeg leaves its
            // multipliers zero).
            var t = c.Qt ?? new int[64];
            var qi = new int[64];
            var qf = new float[64];
            for (int i = 0; i < 64; i++)
            {
                if (method == JpegDctMethod.IntegerFast)
                {
                    int ifastBits = precision == 8 ? 2 : 13;
                    qi[i] = unchecked((t[i] * aanScales[i] + (1 << (14 - ifastBits - 1))) >> (14 - ifastBits));
                }
                else if (method == JpegDctMethod.Float) qf[i] = (float)((double)t[i] * aanFactors[i >> 3] * aanFactors[i & 7]);
                else qi[i] = t[i];
            }
            var plane = new int[dw * dh];
            Span<int> blk = stackalloc int[256];
            var smoothRow = smooth ? new short[c.WidthInBlocks * 64] : null;
            for (int by = 0; by < c.HeightInBlocks; by++)
            {
                if (by * s >= dh) break;
                if (smoothRow != null) LjSmoothRow(c, by, totalImcuRows, lastGoodRow, scans > 1, smoothRow);
                for (int bx = 0; bx < c.WidthInBlocks; bx++)
                {
                    if (bx * s >= dw) break;
                    var src = smoothRow != null ? smoothRow.AsSpan(bx * 64, 64) : c.Coef.AsSpan((by * c.AllocW + bx) * 64, 64);
                    switch (s)
                    {
                        case 1: LjIdct1x1(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 2: LjIdct2x2(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 3: LjIdct3x3(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 4: LjIdct4x4(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 5: LjIdct5x5(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 6: LjIdct6x6(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 7: LjIdct7x7(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 8:
                            if (method == JpegDctMethod.IntegerFast) LjIdctIfast(src, qi, blk, s, rlPost!, mask, p1, center);
                            else if (method == JpegDctMethod.Float) LjIdctFloat(src, qf, blk, s, rlSimple!, mask, p1, center);
                            else LjIdctIslow(src, qi, blk, s, rlPost!, mask, p1, center);
                            break;
                        case 9: LjIdct9x9(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 10: LjIdct10x10(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 11: LjIdct11x11(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 12: LjIdct12x12(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 13: LjIdct13x13(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 14: LjIdct14x14(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        case 15: LjIdct15x15(src, qi, blk, s, rlPost!, mask, p1, center); break;
                        default: LjIdct16x16(src, qi, blk, s, rlPost!, mask, p1, center); break;
                    }
                    for (int y = 0; y < s && by * s + y < dh; y++)
                        for (int x = 0; x < s && bx * s + x < dw; x++)
                            plane[(by * s + y) * dw + bx * s + x] = blk[y * s + x];
                }
            }
            planes[ci] = plane;
        }

        // Upsampling to the output size (jdsample.c: the row-group ratios after IDCT scaling pick the method; no fancy
        // upsampling at 1/8 scale).
        bool doFancy = !lossless && opts.FancyUpsampling && minS > 1;
        var full = new int[nComp][];
        for (int ci = 0; ci < nComp; ci++)
        {
            if (!needed[ci]) continue;
            var c = comps[ci];
            int s = lossless ? 1 : ssize[ci], m = lossless ? 1 : minS;
            full[ci] = LjUpsample(planes[ci], dws[ci], dhs[ci], c.H * s / m, c.V * s / m, maxH, maxV, outW, outH, doFancy)
                ?? throw new NotSupportedException("Fractional sampling not implemented yet");
        }

        // Colour conversion + output.
        var frame = new ImageFrame();
        frame.Initialize(outW, outH, space >= 3 ? ColorspaceType.CMYK : ColorspaceType.SRGB, false);
        int nch = frame.NumberOfChannels;
        int[]? crR = null, cbB = null, crG = null, cbG = null;
        if (space is 1 or 4 && !greyOut) LjYccTables(max, out crR, out cbB, out crG, out cbG);
        const int scaleBits = 16;
        static int Fix(double x) => (int)(x * (1 << scaleBits) + 0.5);
        int gr = Fix(0.29900), gg = Fix(0.58700), gb = Fix(0.11400);
        for (int y = 0; y < outH; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < outW; x++)
            {
                int i = y * outW + x, o = x * nch;
                if (greyOut)
                {
                    // jdcolor.c grayscale_convert (Y) / rgb_gray_convert
                    int v = space == 2
                        ? (gr * full[0][i] + gg * full[1][i] + gb * full[2][i] + (1 << (scaleBits - 1))) >> scaleBits
                        : full[0][i];
                    row[o] = row[o + 1] = row[o + 2] = Scale16(v, max);
                    continue;
                }
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
        else if (ss != 0 || se != 63 || ah != 0 || al != 0)
            LjWarn("Invalid SOS parameters for sequential JPEG");   // jdhuff.c / jdarith.c start_pass: JWRN_NOT_SEQUENTIAL
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

    // jdhuff.c decode_mcu: sequential Huffman MCUs. libjpeg decodes an MCU on its fast path (decode_mcu_fast) when there
    // is no restart interval, 512 bytes per block are buffered and no marker is pending, and redoes it on the slow path
    // if the fast path meets a marker. Both give the same coefficients; they differ in how far the data is read ahead
    // (the bytes later reported as discarded) and in the fast path decoding corrupt codes without a warning.
    private static void DecodeSeqHuffScan(LjBits b, LjComp[] sc, LjHuff?[] dcH, LjHuff?[] acH, int width, int height,
        int maxH, int maxV, int restartInterval)
    {
        foreach (var c in sc) c.LastDc = 0;
        int restartsToGo = restartInterval;
        int blocksInMcu = sc.Length == 1 ? 1 : sc.Sum(c => c.H * c.V);
        var dcs = new int[sc.Length];
        foreach (var mcu in LjMcus(sc, width, height, maxH, maxV))
        {
            if (!b.Insufficient) b.LastGoodRow = mcu[0].Blk / mcu[0].C.AllocW / mcu[0].C.V;
            bool useFast = true;
            if (restartInterval != 0)
            {
                if (restartsToGo == 0)
                {
                    b.Restart();
                    foreach (var cc in sc) cc.LastDc = 0;
                    restartsToGo = restartInterval;
                }
                useFast = false;
            }
            if (b.BytesInBuffer < 512L * blocksInMcu || b.UnreadMarker != 0) useFast = false;
            if (!b.Insufficient)
            {
                if (useFast)
                {
                    var snap = b.Save();
                    for (int i = 0; i < sc.Length; i++) dcs[i] = sc[i].LastDc;
                    b.Fast = true;
                    DecodeSeqMcu(b, mcu, dcH, acH);
                    b.Fast = false;
                    if (b.FastMarker)
                    {
                        b.Load(snap);
                        for (int i = 0; i < sc.Length; i++) sc[i].LastDc = dcs[i];
                        DecodeSeqMcu(b, mcu, dcH, acH);
                    }
                }
                else DecodeSeqMcu(b, mcu, dcH, acH);
            }
            if (restartInterval != 0) restartsToGo--;
        }
    }

    private static void DecodeSeqMcu(LjBits b, (LjComp C, int Blk)[] mcu, LjHuff?[] dcH, LjHuff?[] acH)
    {
        foreach (var (c, blk) in mcu)
        {
            var coef = c.Coef.AsSpan(blk * 64, 64);
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
        }
    }

    // The MCUs of a scan, each as its blocks (component, block index) in decoding order.
    private static IEnumerable<(LjComp C, int Blk)[]> LjMcus(LjComp[] sc, int width, int height, int maxH, int maxV)
    {
        var arr = LjBlocks(sc, width, height, maxH, maxV, static () => { }).ToArray();
        int per = sc.Length == 1 ? 1 : sc.Sum(c => c.H * c.V);
        for (int i = 0; i < arr.Length; i += per) yield return arr[i..(i + per)];
    }

    private static void DecodeHuffScan(LjBits b, LjComp[] sc, LjHuff?[] dcH, LjHuff?[] acH, int width, int height,
        int maxH, int maxV, int restartInterval, bool progressive, int ss, int se, int ah, int al)
    {
        if (!progressive)
        {
            DecodeSeqHuffScan(b, sc, dcH, acH, width, height, maxH, maxV, restartInterval);
            return;
        }
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
                        if (s != 0)
                        {
                            if (s != 1) LjWarn("Corrupt JPEG data: bad Huffman code");
                            s = b.GetBits(1) != 0 ? p1 : m1;
                        }
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
                            if ((m <<= 1) == 0x8000) { LjWarn("Corrupt JPEG data: bad arithmetic code"); error = true; break; }
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
                        if (k > se) { LjWarn("Corrupt JPEG data: bad arithmetic code"); error = true; break; }
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
                    if (k > k1) { LjWarn("Corrupt JPEG data: bad arithmetic code"); error = true; break; }
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
                        if ((m <<= 1) == 0x8000) { LjWarn("Corrupt JPEG data: bad arithmetic code"); error = true; break; }
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

    // jdsample.c: a component's samples (sw x sh) to the output size (w x h). hIn / vIn are its row-group size after
    // IDCT scaling, hOut / vOut the maximum sampling factors: equal = full size, 2:1 = h2v1 / h1v2 / h2v2 (triangle
    // filters when fancy, else replication), other integral ratios replicate. Null for fractional ratios.
    private static int[]? LjUpsample(int[] src, int sw, int sh, int hIn, int vIn, int hOut, int vOut, int w, int h, bool fancy)
    {
        if (hIn == hOut && vIn == vOut)
        {
            if (sw == w && sh == h) return src;
            var crop = new int[w * h];
            for (int y = 0; y < h; y++) Array.Copy(src, y * sw, crop, y * w, w);
            return crop;
        }
        if (hOut % hIn != 0 || vOut % vIn != 0) return null;
        int rx = hOut / hIn, ry = vOut / vIn;
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
