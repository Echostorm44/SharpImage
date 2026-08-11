// JPEG XL codestream front-end: SizeHeader + ImageMetadata + FrameHeader + TOC parsing, then the
// LfGlobal Modular decode. Ported from libjxl (headers.cc, image_metadata.cc, frame_header.cc,
// color_encoding_internal.cc, toc.cc, dec_frame.cc) and validated against a Python prototype that
// reaches the byte-exact Modular section on libjxl-produced lossless files.
using System;
using System.Collections.Generic;
using E = SharpImage.Formats.Jxl.JxlBitReader.U32Enc;

namespace SharpImage.Formats.Jxl;

/// <summary>Result of decoding a lossless Modular JXL frame.</summary>
internal sealed class JxlModularResult
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int NumChannels { get; init; }
    public List<JxlChannel> Channels { get; init; } = [];
    public bool Gray { get; init; }
    public bool HasAlpha { get; init; }
}

internal static class JxlFrame
{
    private static readonly (int Rn, int Rd)[] Ratios =
        [(1, 1), (12, 10), (4, 3), (3, 2), (16, 9), (5, 4), (2, 1)];

    private static (int W, int H) ReadSize(JxlBitReader br)
    {
        bool small = br.ReadBool();
        int ys = small ? ((int)br.ReadBits(5) + 1) * 8 : (int)br.ReadU32(E.BitsOff(9, 1), E.BitsOff(13, 1), E.BitsOff(18, 1), E.BitsOff(30, 1));
        uint ratio = br.ReadBits(3);
        int xs;
        if (ratio == 0)
        {
            xs = small ? ((int)br.ReadBits(5) + 1) * 8 : (int)br.ReadU32(E.BitsOff(9, 1), E.BitsOff(13, 1), E.BitsOff(18, 1), E.BitsOff(30, 1));
        }
        else
        {
            var (rn, rd) = Ratios[ratio - 1];
            xs = ys * rn / rd;
        }

        return (xs, ys);
    }

    private static (int Bits, int Exp) ReadBitDepth(JxlBitReader br)
    {
        bool floating = br.ReadBool();
        if (!floating)
        {
            return ((int)br.ReadU32(E.Val(8), E.Val(10), E.Val(12), E.BitsOff(6, 1)), 0);
        }

        int b = (int)br.ReadU32(E.Val(32), E.Val(16), E.Val(24), E.BitsOff(6, 1));
        return (b, (int)br.ReadBits(4) + 1);
    }

    private static void ReadCustomXy(JxlBitReader br)
    {
        br.ReadU32(E.BitsOff(19, 0), E.BitsOff(19, 524288), E.BitsOff(20, 1048576), E.BitsOff(21, 2097152));
        br.ReadU32(E.BitsOff(19, 0), E.BitsOff(19, 524288), E.BitsOff(20, 1048576), E.BitsOff(21, 2097152));
    }

    private static bool ReadColorEncoding(JxlBitReader br)
    {
        // Returns isGray.
        if (br.ReadBool())
        {
            return false; // all_default sRGB RGB
        }

        bool wantIcc = br.ReadBool();
        int cs = (int)br.ReadEnum(); // 0=RGB,1=Gray,2=XYB,3=Unknown
        if (!wantIcc)
        {
            bool implicitWhite = cs == 2;
            if (!implicitWhite)
            {
                uint wp = br.ReadEnum();
                if (wp == 2)
                {
                    ReadCustomXy(br); // kCustom white point
                }
            }

            bool hasPrimaries = cs == 0;
            if (hasPrimaries)
            {
                uint pr = br.ReadEnum();
                if (pr == 2)
                {
                    ReadCustomXy(br);
                    ReadCustomXy(br);
                    ReadCustomXy(br);
                }
            }

            // Transfer function (implicit for XYB).
            if (cs != 2)
            {
                bool haveGamma = br.ReadBool();
                if (haveGamma)
                {
                    br.ReadBits(24);
                }
                else
                {
                    br.ReadEnum();
                }
            }

            br.ReadEnum(); // rendering intent
        }

        return cs == 1;
    }

    private static void ReadToneMapping(JxlBitReader br)
    {
        if (br.ReadBool())
        {
            return; // all_default
        }

        br.ReadF16();
        br.ReadF16();
        br.ReadBool();
        br.ReadF16();
    }

    private static void ReadExtensions(JxlBitReader br)
    {
        ulong ext = br.ReadU64();
        if (ext != 0)
        {
            var sizes = new List<ulong>();
            for (int i = 0; i < 64; i++)
            {
                if ((ext & (1UL << i)) != 0)
                {
                    sizes.Add(br.ReadU64());
                }
            }

            foreach (ulong sz in sizes)
            {
                br.Consume((int)(sz * 8));
            }
        }
    }

    private static void ReadName(JxlBitReader br)
    {
        int len = (int)br.ReadU32(E.Val(0), E.BitsOff(4, 0), E.BitsOff(5, 16), E.BitsOff(10, 48));
        for (int i = 0; i < len; i++)
        {
            br.ReadBits(8);
        }
    }

    private static void ReadExtraChannel(JxlBitReader br)
    {
        if (br.ReadBool())
        {
            return; // all_default alpha
        }

        uint type = br.ReadEnum();
        ReadBitDepth(br);
        br.ReadU32(E.Val(0), E.Val(3), E.Val(4), E.BitsOff(3, 1)); // dim_shift
        ReadName(br);
        if (type == 0)
        {
            br.ReadBool(); // alpha_associated
        }

        if (type == 4)
        {
            for (int i = 0; i < 4; i++)
            {
                br.ReadF16();
            }
        }

        if (type == 5)
        {
            br.ReadU32(E.Val(1), E.BitsOff(2, 0), E.BitsOff(4, 3), E.BitsOff(8, 19));
        }
    }

    private sealed class Meta
    {
        public int Width, Height, Bps = 8, Extra;
        public bool Xyb = true, Gray;
    }

    private static Meta ReadImageMetadata(JxlBitReader br, int w, int h)
    {
        var md = new Meta { Width = w, Height = h };
        if (br.ReadBool())
        {
            return md; // all_default
        }

        bool extraFields = br.ReadBool();
        if (extraFields)
        {
            br.ReadBits(3); // orientation - 1
            if (br.ReadBool())
            {
                ReadSize(br); // intrinsic
            }

            if (br.ReadBool())
            {
                ReadSize(br); // preview
            }

            if (br.ReadBool())
            {
                br.ReadU32(E.Val(100), E.Val(1000), E.BitsOff(10, 1), E.BitsOff(30, 1));
                br.ReadU32(E.Val(1), E.Val(1001), E.BitsOff(8, 1), E.BitsOff(10, 1));
                br.ReadU32(E.Val(0), E.BitsOff(3, 0), E.BitsOff(16, 0), E.BitsOff(32, 0));
                br.ReadBool();
            }
        }

        (int bits, _) = ReadBitDepth(br);
        md.Bps = bits;
        br.ReadBool(); // modular_16bit_buffer_sufficient
        md.Extra = (int)br.ReadU32(E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(12, 1));
        for (int i = 0; i < md.Extra; i++)
        {
            ReadExtraChannel(br);
        }

        md.Xyb = br.ReadBool();
        md.Gray = ReadColorEncoding(br);
        if (extraFields)
        {
            ReadToneMapping(br);
        }

        ReadExtensions(br);

        // ImageMetadata tail: default_m (always), opsin matrix (only if xyb), custom upsampling weights.
        bool defaultM = br.ReadBool();
        if (!defaultM)
        {
            if (md.Xyb)
            {
                if (!br.ReadBool())
                {
                    for (int i = 0; i < 9 + 3 + 4; i++)
                    {
                        br.ReadF16(); // opsin inverse matrix + biases + quant biases
                    }
                }
            }

            uint cwMask = br.ReadBits(3);
            if ((cwMask & 1) != 0)
            {
                for (int i = 0; i < 15; i++)
                {
                    br.ReadF16();
                }
            }

            if ((cwMask & 2) != 0)
            {
                for (int i = 0; i < 55; i++)
                {
                    br.ReadF16();
                }
            }

            if ((cwMask & 4) != 0)
            {
                for (int i = 0; i < 210; i++)
                {
                    br.ReadF16();
                }
            }
        }

        return md;
    }

    // --- Frame header (jxl-oxide header.rs field order) ---
    private sealed class FrameInfo
    {
        public bool IsModular;
        public int GroupSizeShift = 1;
        public int NumPasses = 1;
    }

    private static int ReadPasses(JxlBitReader br)
    {
        int num = (int)br.ReadU32(E.Val(1), E.Val(2), E.Val(3), E.BitsOff(3, 4));
        if (num != 1)
        {
            int nd = (int)br.ReadU32(E.Val(0), E.Val(1), E.Val(2), E.BitsOff(1, 3));
            for (int i = 0; i < num - 1; i++)
            {
                br.ReadBits(2);
            }

            for (int i = 0; i < nd; i++)
            {
                br.ReadU32(E.Val(1), E.Val(2), E.Val(4), E.Val(8));
            }

            for (int i = 0; i < nd; i++)
            {
                br.ReadU32(E.Val(0), E.Val(1), E.Val(2), E.BitsOff(3, 0));
            }
        }

        return num;
    }

    private static void ReadBlendingInfo(JxlBitReader br, int numEc)
    {
        uint mode = br.ReadU32(E.Val(0), E.Val(1), E.Val(2), E.BitsOff(2, 3));
        if (numEc > 0 && (mode == 2 || mode == 3))
        {
            br.ReadU32(E.Val(0), E.Val(1), E.Val(2), E.BitsOff(3, 3));
        }

        if ((numEc > 0 && (mode == 2 || mode == 3)) || mode == 4)
        {
            br.ReadBool();
        }

        if (mode != 0)
        {
            br.ReadBits(2); // source
        }
    }

    private static void ReadLoopFilter(JxlBitReader br, bool isModular)
    {
        if (br.ReadBool())
        {
            return; // all_default
        }

        bool gab = br.ReadBool();
        if (gab && br.ReadBool())
        {
            for (int i = 0; i < 6; i++)
            {
                br.ReadF16();
            }
        }

        int epf = (int)br.ReadBits(2);
        if (epf > 0)
        {
            if (!isModular && br.ReadBool())
            {
                for (int i = 0; i < 8; i++)
                {
                    br.ReadF16();
                }
            }

            if (br.ReadBool())
            {
                for (int i = 0; i < 5; i++)
                {
                    br.ReadF16();
                }
            }

            if (br.ReadBool())
            {
                if (!isModular)
                {
                    br.ReadF16();
                }

                br.ReadF16();
                br.ReadF16();
                br.ReadF16();
            }

            if (isModular)
            {
                br.ReadF16();
            }
        }

        ReadExtensions(br);
    }

    private static FrameInfo ReadFrameHeader(JxlBitReader br, Meta md)
    {
        var fh = new FrameInfo();
        if (br.ReadBool())
        {
            // all_default: regular VarDCT frame.
            fh.IsModular = false;
            return fh;
        }

        int frameType = (int)br.ReadBits(2); // 0=Regular,1=LfFrame,2=RefOnly,3=SkipProg
        fh.IsModular = br.ReadBits(1) == 1;   // encoding: 0=VarDct, 1=Modular
        ulong flags = br.ReadU64();
        bool useLf = (flags & 0x20) != 0;
        bool doYcbcr = false;
        if (!md.Xyb)
        {
            doYcbcr = br.ReadBool();
        }

        if (doYcbcr && !useLf)
        {
            for (int i = 0; i < 3; i++)
            {
                br.ReadBits(2); // jpeg_upsampling
            }
        }

        if (!useLf)
        {
            br.ReadU32(E.Val(1), E.Val(2), E.Val(4), E.Val(8)); // upsampling
            for (int i = 0; i < md.Extra; i++)
            {
                br.ReadU32(E.Val(1), E.Val(2), E.Val(4), E.Val(8));
            }
        }

        if (fh.IsModular)
        {
            fh.GroupSizeShift = (int)br.ReadBits(2);
        }
        else if (md.Xyb)
        {
            br.ReadBits(3);
            br.ReadBits(3); // x_qm, b_qm
        }

        if (frameType != 2)
        {
            fh.NumPasses = ReadPasses(br);
        }

        if (frameType == 1)
        {
            br.ReadBits(2); // lf_level
        }

        bool isNormal = frameType == 0 || frameType == 3;
        if (frameType != 1)
        {
            bool haveCrop = br.ReadBool();
            if (haveCrop)
            {
                E c0 = E.BitsOff(8, 0), c1 = E.BitsOff(11, 256), c2 = E.BitsOff(14, 2304), c3 = E.BitsOff(30, 18688);
                if (frameType != 2)
                {
                    br.ReadU32(c0, c1, c2, c3);
                    br.ReadU32(c0, c1, c2, c3);
                }

                br.ReadU32(c0, c1, c2, c3);
                br.ReadU32(c0, c1, c2, c3);
            }
        }

        bool isLast = frameType == 0;
        if (isNormal)
        {
            ReadBlendingInfo(br, md.Extra);
            for (int i = 0; i < md.Extra; i++)
            {
                ReadBlendingInfo(br, md.Extra);
            }

            isLast = br.ReadBool();
        }

        if (frameType != 1 && !isLast)
        {
            br.ReadBits(2); // save_as_reference
        }

        if (frameType == 2)
        {
            br.ReadBool(); // save_before_ct
        }

        ReadName(br);
        ReadLoopFilter(br, fh.IsModular);
        ReadExtensions(br);
        return fh;
    }

    private static readonly E TocDist0 = E.BitsOff(10, 0);
    private static readonly E TocDist1 = E.BitsOff(14, 1024);
    private static readonly E TocDist2 = E.BitsOff(22, 17408);
    private static readonly E TocDist3 = E.BitsOff(30, 4211712);

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    /// <summary>Decodes a single-frame lossless Modular codestream (signature already at offset 0).</summary>
    public static JxlModularResult DecodeModularCodestream(byte[] cs)
    {
        if (cs.Length < 2 || cs[0] != 0xFF || cs[1] != 0x0A)
        {
            throw new InvalidOperationException("Not a JPEG XL codestream.");
        }

        var br = new JxlBitReader(cs, 2);
        (int w, int h) = ReadSize(br);
        Meta md = ReadImageMetadata(br, w, h);

        // The frame body (FrameHeader + TOC + sections) is byte-aligned after ImageMetadata.
        br.JumpToByteBoundary();
        FrameInfo fh = ReadFrameHeader(br, md);
        if (!fh.IsModular)
        {
            throw new NotSupportedException("JPEG XL VarDCT (lossy) frames are not yet supported.");
        }

        int groupDim = 128 << fh.GroupSizeShift;
        int numGroups = CeilDiv(w, groupDim) * CeilDiv(h, groupDim);
        int lfDim = groupDim * 8;
        int numLf = CeilDiv(w, lfDim) * CeilDiv(h, lfDim);
        int entryCount = (numGroups == 1 && fh.NumPasses == 1)
            ? 1
            : 1 + numLf + 1 + (numGroups * fh.NumPasses);

        // TOC.
        if (br.ReadBool())
        {
            throw new NotSupportedException("JPEG XL permuted TOC is not yet supported.");
        }

        br.JumpToByteBoundary();
        uint[] sizes = new uint[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            sizes[i] = br.ReadU32(TocDist0, TocDist1, TocDist2, TocDist3);
        }

        br.JumpToByteBoundary();
        int baseByte = (int)(br.BitPosition / 8);
        int[] offsets = new int[entryCount];
        int acc = baseByte;
        for (int i = 0; i < entryCount; i++)
        {
            offsets[i] = acc;
            acc += (int)sizes[i];
        }

        int nbChans = md.Gray ? 1 : 3;
        List<JxlChannel> chans;
        try
        {
            chans = entryCount == 1
                ? JxlModular.DecodeGlobalModular(cs, offsets[0], w, h, nbChans, md.Bps)
                : JxlModular.DecodeMultiGroup(cs, offsets, w, h, nbChans, md.Bps, groupDim, numGroups, numLf, fh.NumPasses);
        }
        catch (Exception e) when (e is not NotSupportedException)
        {
            throw new NotSupportedException("This JPEG XL frame is not a supported lossless Modular frame.", e);
        }

        return new JxlModularResult
        {
            Width = w,
            Height = h,
            NumChannels = nbChans,
            Channels = chans,
            Gray = md.Gray,
        };
    }
}
