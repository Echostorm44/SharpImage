// JPEG XL codestream headers (ISO/IEC 18181-1 §D): SizeHeader + ImageMetadata, ported from
// libjxl (headers.cc, image_metadata.cc, color_encoding_internal.cc). Reads the image size,
// bit depth, extra-channel count and the XYB/colour-encoding info that follow the 0xFF 0x0A
// codestream signature.
using System;
using System.IO;
using E = SharpImage.Formats.Jxl.JxlBitReader.U32Enc;

namespace SharpImage.Formats.Jxl;

/// <summary>Basic image info decoded from the JXL codestream header.</summary>
internal sealed class JxlImageInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitsPerSample { get; init; }
    public int ExponentBits { get; init; }
    public int ExtraChannels { get; init; }
    public bool XybEncoded { get; init; }
    public int ColorSpace { get; init; }   // 0=RGB, 1=Gray, 2=XYB, 3=Unknown
}

internal static class JxlHeaders
{
    private static readonly (int Rn, int Rd)[] Ratios =
        [(1, 1), (12, 10), (4, 3), (3, 2), (16, 9), (5, 4), (2, 1)];

    /// <summary>Reads the codestream signature + SizeHeader + ImageMetadata from a raw JXL codestream.</summary>
    public static JxlImageInfo ReadHeader(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0xFF || data[1] != 0x0A)
        {
            throw new InvalidDataException("Not a JPEG XL codestream (missing 0xFF 0x0A signature).");
        }

        var br = new JxlBitReader(data, 2);
        (int w, int h) = ReadSize(br);
        return ReadImageMetadata(br, w, h);
    }

    private static (int W, int H) ReadSize(JxlBitReader br)
    {
        bool small = br.ReadBool();
        int ysize = small
            ? (int)((br.ReadBits(5) + 1) * 8)
            : (int)br.ReadU32(E.BitsOff(9, 1), E.BitsOff(13, 1), E.BitsOff(18, 1), E.BitsOff(30, 1));

        uint ratio = br.ReadBits(3);
        int xsize;
        if (ratio == 0)
        {
            xsize = small
                ? (int)((br.ReadBits(5) + 1) * 8)
                : (int)br.ReadU32(E.BitsOff(9, 1), E.BitsOff(13, 1), E.BitsOff(18, 1), E.BitsOff(30, 1));
        }
        else
        {
            var (rn, rd) = Ratios[ratio - 1];
            xsize = ysize * rn / rd;
        }

        return (xsize, ysize);
    }

    private static (int Bits, int ExpBits) ReadBitDepth(JxlBitReader br)
    {
        bool floating = br.ReadBool();
        if (!floating)
        {
            int bits = (int)br.ReadU32(E.Val(8), E.Val(10), E.Val(12), E.BitsOff(6, 1));
            return (bits, 0);
        }

        int fbits = (int)br.ReadU32(E.Val(32), E.Val(16), E.Val(24), E.BitsOff(6, 1));
        int exp = (int)br.ReadBits(4) + 1;
        return (fbits, exp);
    }

    private static void SkipColorEncoding(JxlBitReader br, bool xyb)
    {
        if (br.ReadBool())
        {
            return; // all_default
        }

        bool wantIcc = br.ReadBool();
        int cs = (int)br.ReadEnum(); // 0=RGB,1=Gray,2=XYB,3=Unknown
        if (!wantIcc)
        {
            bool implicitWhite = cs == 2; // XYB
            if (!implicitWhite)
            {
                uint wp = br.ReadEnum();
                if (wp == 1)
                {
                    br.ReadF16();
                    br.ReadF16(); // custom white point (x, y)
                }
            }

            bool hasPrimaries = cs == 0; // RGB
            if (hasPrimaries)
            {
                uint pr = br.ReadEnum();
                if (pr == 1)
                {
                    for (int i = 0; i < 6; i++)
                    {
                        br.ReadF16(); // custom red/green/blue chromaticities
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
                    br.ReadEnum();
                }
            }

            br.ReadEnum(); // rendering intent
        }
    }

    private static JxlImageInfo ReadImageMetadata(JxlBitReader br, int w, int h)
    {
        if (br.ReadBool())
        {
            // all_default: 8-bit, sRGB, XYB-encoded, no extra channels.
            return new JxlImageInfo { Width = w, Height = h, BitsPerSample = 8, XybEncoded = true, ColorSpace = 0 };
        }

        bool extraFields = br.ReadBool();
        if (extraFields)
        {
            br.ReadBits(3); // orientation - 1
            if (br.ReadBool())
            {
                ReadSize(br); // intrinsic size
            }

            if (br.ReadBool())
            {
                ReadSize(br); // preview size (uses a slightly different encoding; approximate)
            }

            if (br.ReadBool())
            {
                // animation: tps_numerator (U32), tps_denominator (U32), num_loops (U32), have_timecodes (Bool)
                br.ReadU32(E.Val(100), E.Val(1000), E.BitsOff(10, 1), E.BitsOff(30, 1));
                br.ReadU32(E.Val(1), E.Val(1001), E.BitsOff(8, 1), E.BitsOff(10, 1));
                br.ReadU32(E.Val(0), E.BitsOff(3, 0), E.BitsOff(16, 0), E.BitsOff(32, 0));
                br.ReadBool();
            }
        }

        (int bits, int exp) = ReadBitDepth(br);
        br.ReadBool(); // modular_16bit_buffer_sufficient
        int extra = (int)br.ReadU32(E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(12, 1));
        // Extra-channel infos would be read here when extra != 0 (not yet needed for the
        // common RGB/lossless path); left for the next stage of the decoder.
        bool xyb = br.ReadBool();
        SkipColorEncoding(br, xyb);

        return new JxlImageInfo
        {
            Width = w,
            Height = h,
            BitsPerSample = bits,
            ExponentBits = exp,
            ExtraChannels = extra,
            XybEncoded = xyb,
        };
    }
}
