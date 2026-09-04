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
    public int Bps { get; init; } = 8; // bits per sample of the decoded channel values
    public Core.ColorspaceType Colorspace { get; init; } = Core.ColorspaceType.SRGB;
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

    private static (double X, double Y) ReadCustomXy(JxlBitReader br)
    {
        int x = JxlBits.UnpackSigned(br.ReadU32(E.BitsOff(19, 0), E.BitsOff(19, 524288), E.BitsOff(20, 1048576), E.BitsOff(21, 2097152)));
        int y = JxlBits.UnpackSigned(br.ReadU32(E.BitsOff(19, 0), E.BitsOff(19, 524288), E.BitsOff(20, 1048576), E.BitsOff(21, 2097152)));
        return (x / 1e6, y / 1e6);
    }

    private static Core.ColorspaceType ReadColorEncoding(JxlBitReader br)
    {
        if (br.ReadBool())
        {
            return Core.ColorspaceType.SRGB; // all_default sRGB RGB
        }

        bool wantIcc = br.ReadBool();
        int cs = (int)br.ReadEnum(); // 0=RGB,1=Gray,2=XYB,3=Unknown
        int primaries = 1, transfer = 13;
        bool customIsAdobe = false;
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
                primaries = (int)br.ReadEnum();
                if (primaries == 2)
                {
                    (double rx, double ry) = ReadCustomXy(br);
                    ReadCustomXy(br); // green
                    ReadCustomXy(br); // blue
                    // Recognise Adobe RGB (1998) by its red primary (0.64, 0.33) so it round-trips.
                    if (Math.Abs(rx - 0.64) < 0.01 && Math.Abs(ry - 0.33) < 0.01)
                    {
                        customIsAdobe = true;
                    }
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
                    transfer = (int)br.ReadEnum();
                }
            }

            br.ReadEnum(); // rendering intent
        }

        // Map the parsed encoding to a ColorspaceType (best effort; ICC/custom fall back to sRGB/Gray).
        if (cs == 1)
        {
            return transfer == 8 ? Core.ColorspaceType.LinearGray : Core.ColorspaceType.Gray;
        }

        if (!wantIcc && customIsAdobe)
        {
            return Core.ColorspaceType.Adobe98;
        }

        if (!wantIcc && primaries == 11)
        {
            return Core.ColorspaceType.DisplayP3; // kP3
        }

        if (!wantIcc && transfer == 8)
        {
            return Core.ColorspaceType.ScRGB; // linear sRGB
        }

        return Core.ColorspaceType.SRGB;
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
        public bool HaveAnimation;
        public uint TpsNum = 1, TpsDenom = 1, NumLoops;
        public Core.ColorspaceType Colorspace = Core.ColorspaceType.SRGB;
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

            if (br.ReadBool()) // have_animation
            {
                md.HaveAnimation = true;
                md.TpsNum = br.ReadU32(E.Val(100), E.Val(1000), E.BitsOff(10, 1), E.BitsOff(30, 1));
                md.TpsDenom = br.ReadU32(E.Val(1), E.Val(1001), E.BitsOff(8, 1), E.BitsOff(10, 1));
                md.NumLoops = br.ReadU32(E.Val(0), E.BitsOff(3, 0), E.BitsOff(16, 0), E.BitsOff(32, 0));
                br.ReadBool(); // have_timecodes
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
        md.Colorspace = ReadColorEncoding(br);
        md.Gray = md.Colorspace is Core.ColorspaceType.Gray or Core.ColorspaceType.LinearGray;
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
        public int[] PassShift = { 0 };
        public ulong Flags;
        public bool IsLast = true;
        public uint Duration; // animation: frame duration in ticks
        public int XQmScale = 2;
        public int BQmScale = 2;

        // Loop filter params (JXL spec defaults; overwritten by ReadLoopFilter for non-default frames).
        public bool GabEnabled = true;
        public float[][] GabWeights = { new[] { 0.115169525f, 0.061248592f }, new[] { 0.115169525f, 0.061248592f }, new[] { 0.115169525f, 0.061248592f } };
        public int EpfIters = 2;
        public float[] EpfChannelScale = { 40.0f, 5.0f, 3.5f };
        public float[] EpfSharpLut = { 0f, 1f / 7f, 2f / 7f, 3f / 7f, 4f / 7f, 5f / 7f, 6f / 7f, 1f };
        public float EpfQuantMul = 0.46f;
        public float EpfPass0SigmaScale = 0.9f;
        public float EpfPass2SigmaScale = 6.5f;
        public float EpfBorderSadMul = 2.0f / 3.0f;
    }

    private static int ReadPasses(JxlBitReader br, out int[] shift)
    {
        int num = (int)br.ReadU32(E.Val(1), E.Val(2), E.Val(3), E.BitsOff(3, 4));
        shift = new int[num]; // shift[num-1] stays 0 (final pass is always full precision)
        if (num != 1)
        {
            int nd = (int)br.ReadU32(E.Val(0), E.Val(1), E.Val(2), E.BitsOff(1, 3));
            for (int i = 0; i < num - 1; i++)
            {
                shift[i] = (int)br.ReadBits(2);
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

    private static void ReadLoopFilter(JxlBitReader br, FrameInfo fh)
    {
        bool isModular = fh.IsModular;
        if (br.ReadBool())
        {
            return; // all_default: FrameInfo keeps spec-default loop filter fields
        }

        // Gaborish
        bool gab = br.ReadBool();
        fh.GabEnabled = gab;
        if (gab && br.ReadBool())
        {
            // custom weights: 3 channels x (w0, w1)
            for (int c = 0; c < 3; c++)
            {
                fh.GabWeights[c][0] = br.ReadF16();
                fh.GabWeights[c][1] = br.ReadF16();
            }
        }

        // Edge-preserving filter
        int iters = (int)br.ReadBits(2);
        fh.EpfIters = iters;
        if (iters > 0)
        {
            if (!isModular && br.ReadBool())
            {
                // custom sharpness LUT (VarDct only)
                for (int i = 0; i < 8; i++)
                {
                    fh.EpfSharpLut[i] = br.ReadF16();
                }
            }

            if (br.ReadBool())
            {
                // custom channel scale
                for (int i = 0; i < 3; i++)
                {
                    fh.EpfChannelScale[i] = br.ReadF16();
                }

                br.ReadBits(32); // reserved/ignored
            }

            if (br.ReadBool())
            {
                // custom sigma params
                if (!isModular)
                {
                    fh.EpfQuantMul = br.ReadF16();
                }

                fh.EpfPass0SigmaScale = br.ReadF16();
                fh.EpfPass2SigmaScale = br.ReadF16();
                fh.EpfBorderSadMul = br.ReadF16();
            }

            if (isModular)
            {
                br.ReadF16(); // sigma_for_modular
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
        fh.Flags = flags;
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
            fh.XQmScale = (int)br.ReadBits(3);
            fh.BQmScale = (int)br.ReadBits(3);
        }

        if (frameType != 2)
        {
            fh.NumPasses = ReadPasses(br, out int[] passShift);
            fh.PassShift = passShift;
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

            if (md.HaveAnimation)
            {
                fh.Duration = br.ReadU32(E.Val(0), E.Val(1), E.BitsOff(8, 0), E.BitsOff(32, 0)); // frame duration (ticks)
                // have_timecodes is false at the metadata level, so no timecode field.
            }

            isLast = br.ReadBool();
        }

        fh.IsLast = isLast;

        if (frameType != 1 && !isLast)
        {
            br.ReadBits(2); // save_as_reference
        }

        if (frameType == 2)
        {
            br.ReadBool(); // save_before_ct
        }

        ReadName(br);
        ReadLoopFilter(br, fh);
        ReadExtensions(br);
        return fh;
    }

    private static readonly E TocDist0 = E.BitsOff(10, 0);
    private static readonly E TocDist1 = E.BitsOff(14, 1024);
    private static readonly E TocDist2 = E.BitsOff(22, 17408);
    private static readonly E TocDist3 = E.BitsOff(30, 4211712);

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    /// <summary>Decodes a single-frame JPEG XL codestream (signature already at offset 0). When
    /// <paramref name="allowTruncated"/> is set and the buffer is a prefix of a progressive/multi-section
    /// VarDCT frame, sections that have not fully arrived are treated as zero — yielding a DC-only or
    /// partial-pass preview instead of throwing (the header and TOC must still be present).</summary>
    public static JxlModularResult DecodeModularCodestream(byte[] cs, bool allowTruncated = false)
    {
        if (cs.Length < 2 || cs[0] != 0xFF || cs[1] != 0x0A)
        {
            throw new InvalidOperationException("Not a JPEG XL codestream.");
        }

        var br = new JxlBitReader(cs, 2);
        (int w, int h) = ReadSize(br);
        Meta md = ReadImageMetadata(br, w, h);

        return DecodeFrame(cs, br, w, h, md, allowTruncated, out _, out _);
    }

    // Decodes one frame from `br` (positioned at the frame header). Sets endByte to the offset of the next
    // frame (after the last section) and fh to the frame header (carrying is_last / animation duration).
    private static JxlModularResult DecodeFrame(byte[] cs, JxlBitReader br, int w, int h, Meta md, bool allowTruncated, out int endByte, out FrameInfo fh)
    {
        // The frame body (FrameHeader + TOC + sections) is byte-aligned after the metadata / previous frame.
        br.JumpToByteBoundary();
        fh = ReadFrameHeader(br, md);

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

        endByte = acc; // byte after the last section = start of the next frame (if any)

        if (!fh.IsModular)
        {
            var fp = new VarDctFrameParams
            {
                Width = w,
                Height = h,
                BitDepth = md.Bps,
                GroupDim = groupDim,
                NumGroups = numGroups,
                NumLf = numLf,
                GroupsPerRow = (w + groupDim - 1) / groupDim,
                NumPasses = fh.NumPasses,
                PassShift = fh.PassShift,
                Xyb = md.Xyb,
                Flags = fh.Flags,
                XQmScale = fh.XQmScale,
                BQmScale = fh.BQmScale,
                SkipAdaptiveLfSmoothing = (fh.Flags & 0x80) != 0,
                GabEnabled = fh.GabEnabled,
                GabWeights = fh.GabWeights,
                EpfIters = fh.EpfIters,
                EpfChannelScale = fh.EpfChannelScale,
                EpfSharpLut = fh.EpfSharpLut,
                EpfQuantMul = fh.EpfQuantMul,
                EpfPass0SigmaScale = fh.EpfPass0SigmaScale,
                EpfPass2SigmaScale = fh.EpfPass2SigmaScale,
                EpfBorderSadMul = fh.EpfBorderSadMul,
                NumExtra = md.Extra,
            };
            fp.SetPrimaries(md.Colorspace); // wide-gamut XYB target primaries (e.g. Display P3)
            float[][] rgb = JxlVarDct.Decode(cs, offsets, sizes, fp, null, null, out int[][]? extra, allowTruncated);
            int maxV = (1 << md.Bps) - 1; // honor the declared output depth (e.g. 16-bit XYB files)
            var vc = new List<JxlChannel> { new(w, h), new(w, h), new(w, h) };
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < w * h; i++)
                {
                    vc[c].Px[i] = Math.Clamp((int)MathF.Round(rgb[c][i] * maxV), 0, maxV);
                }
            }

            // A single alpha extra channel is surfaced as a 4th channel (RGBA).
            bool hasAlpha = extra != null && extra.Length >= 1;
            if (hasAlpha)
            {
                var a = new JxlChannel(w, h);
                for (int i = 0; i < w * h; i++)
                {
                    a.Px[i] = Math.Clamp(extra![0][i], 0, maxV);
                }

                vc.Add(a);
            }

            return new JxlModularResult { Width = w, Height = h, NumChannels = hasAlpha ? 4 : 3, Channels = vc, Gray = false, HasAlpha = hasAlpha, Bps = md.Bps, Colorspace = md.Colorspace };
        }

        int nbChans = (md.Gray ? 1 : 3) + md.Extra; // colour channels + extra channels (e.g. alpha)
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
            Bps = md.Bps,
            HasAlpha = md.Extra > 0,
            Colorspace = md.Colorspace,
        };
    }

    /// <summary>Decodes every frame of a (possibly animated) codestream: returns each frame with its
    /// animation duration in ticks (and the loop count / ticks-per-second from the metadata).</summary>
    public static (List<(JxlModularResult Frame, int DurationTicks)> Frames, int NumLoops, uint TpsNum, uint TpsDenom) DecodeSequence(byte[] cs)
    {
        if (cs.Length < 2 || cs[0] != 0xFF || cs[1] != 0x0A)
        {
            throw new InvalidOperationException("Not a JPEG XL codestream.");
        }

        var br = new JxlBitReader(cs, 2);
        (int w, int h) = ReadSize(br);
        Meta md = ReadImageMetadata(br, w, h);

        var frames = new List<(JxlModularResult, int)>();
        int cur = (int)((br.BitPosition + 7) / 8); // first frame is byte-aligned after the metadata
        while (cur < cs.Length)
        {
            var fbr = new JxlBitReader(cs, cur);
            JxlModularResult res = DecodeFrame(cs, fbr, w, h, md, allowTruncated: false, out int endByte, out FrameInfo fh);
            frames.Add((res, (int)fh.Duration));
            cur = endByte;
            if (fh.IsLast)
            {
                break;
            }
        }

        return (frames, (int)md.NumLoops, md.TpsNum, md.TpsDenom);
    }
}
