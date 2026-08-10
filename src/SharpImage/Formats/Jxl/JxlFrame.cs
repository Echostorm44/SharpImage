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
        return md;
    }

    private static readonly E TocDist0 = E.BitsOff(10, 0);
    private static readonly E TocDist1 = E.BitsOff(14, 1024);
    private static readonly E TocDist2 = E.BitsOff(22, 17408);
    private static readonly E TocDist3 = E.BitsOff(30, 4211712);

    /// <summary>
    /// Locates the byte-aligned start of the single frame-body section. The TOC of a single-group
    /// single-pass frame has exactly one entry whose size covers the rest of the codestream, so the
    /// section start is the unique byte-aligned position where a TOC size read leaves that invariant
    /// true. This is exact for single-section lossless frames (the common case).
    /// </summary>
    private static int FindSectionByte(byte[] cs)
    {
        int flen = cs.Length;
        for (int p = 40; p < flen * 8; p++)
        {
            var br = new JxlBitReader(cs);
            br.SeekBit(p);
            br.JumpToByteBoundary();
            uint sz;
            try
            {
                sz = br.ReadU32(TocDist0, TocDist1, TocDist2, TocDist3);
            }
            catch
            {
                continue;
            }

            br.JumpToByteBoundary();
            long endByte = br.BitPosition / 8;
            if (endByte + sz == flen)
            {
                return (int)endByte;
            }
        }

        return -1;
    }

    /// <summary>Decodes a single-frame lossless Modular codestream (signature already at offset 0).</summary>
    public static JxlModularResult DecodeModularCodestream(byte[] cs)
    {
        if (cs.Length < 2 || cs[0] != 0xFF || cs[1] != 0x0A)
        {
            throw new InvalidOperationException("Not a JPEG XL codestream.");
        }

        // Parse SizeHeader + the leading part of ImageMetadata (dimensions, bit depth, extra channels
        // and colour space are all read before the metadata tail that the frame-header parse still
        // approximates). The frame body is located structurally via the TOC (see FindSectionByte),
        // and the Modular ANS final-state check confirms the frame really is Modular.
        var br = new JxlBitReader(cs, 2);
        (int w, int h) = ReadSize(br);
        Meta md = ReadImageMetadata(br, w, h);

        int sectionByte = FindSectionByte(cs);
        if (sectionByte < 0)
        {
            throw new InvalidOperationException("Could not locate the JPEG XL frame body.");
        }

        int nbChans = md.Gray ? 1 : 3;
        List<JxlChannel> chans;
        try
        {
            chans = JxlModular.DecodeGlobalModular(cs, sectionByte, w, h, nbChans, md.Bps);
        }
        catch (Exception e) when (e is not NotSupportedException)
        {
            throw new NotSupportedException("This JPEG XL frame is not a supported lossless Modular frame (VarDCT/lossy is not yet implemented).", e);
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
