// JPEG XL (lossless Modular) encoder: the inverse of the decoder in this directory. It emits a bare
// codestream (0xFF 0x0A) with an 8-bit sRGB ImageMetadata and a Modular frame that applies the
// reversible YCoCg RCT, predicts with the self-correcting weighted predictor (run through the
// decoder's own WpState so residuals match bit-for-bit), and entropy-codes the residuals with prefix
// (Huffman) codes plus LZ77 back-references (to collapse flat/repeating regions). Large images are
// tiled into groups. Verified by decoding the output with the reference decoders (libjxl / jxl-oxide),
// not just round-tripping.
using System;
using System.Collections.Generic;
using SharpImage.Core;
using SharpImage.Image;
using E = SharpImage.Formats.Jxl.JxlBitReader.U32Enc;

namespace SharpImage.Formats.Jxl;

internal static partial class JxlEncoder
{
    // MA-tree leaf predictor: 6 = self-correcting weighted predictor (see JxlModular.PredictOne).
    private const int WeightedPredictor = 6;

    /// <summary>Group side when a single group covers the image (group_size_shift 3 => 1024).</summary>
    private const int GroupDim = 1024;
    private const int GroupSizeShift = 3; // 128 << 3 == 1024

    /// <summary>Encodes an image as a lossless RGB JPEG XL codestream.</summary>
    // Returns the smallest standard bit depth (8 or 16) that represents every sample exactly. Used so 8-bit
    // content stays 8-bit (smaller) while genuine 16-bit content is preserved losslessly / without banding.
    internal static int DetectBits(ImageFrame image)
    {
        int w = (int)image.Columns, h = (int)image.Rows, srcCh = image.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * srcCh;
                for (int c = 0; c < srcCh; c++)
                {
                    ushort q = row[off + c];
                    if (Quantum.ScaleFromByte(Quantum.ScaleToByte(q)) != q)
                    {
                        return 16;
                    }
                }
            }
        }

        return 8;
    }

    public static byte[] EncodeLossless(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        if (w <= 0 || h <= 0)
        {
            throw new InvalidOperationException("Cannot encode an empty image.");
        }

        // Extract RGB (grayscale expanded to RGB). Detect the actual precision so lossless stays lossless:
        // 8-bit if every sample is 8-bit-representable (the common case, smaller files), else full 16-bit.
        int nb = 3;
        int srcCh = image.NumberOfChannels;
        int bits = DetectBits(image);
        int[] r = new int[w * h], g = new int[w * h], b = new int[w * h];
        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * srcCh;
                int p = (y * w) + x;
                if (srcCh == 1)
                {
                    r[p] = g[p] = b[p] = Quantum.ScaleToDepth(row[off], bits);
                }
                else
                {
                    r[p] = Quantum.ScaleToDepth(row[off], bits);
                    g[p] = Quantum.ScaleToDepth(row[off + 1], bits);
                    b[p] = Quantum.ScaleToDepth(row[off + 2], bits);
                }
            }
        }

        // Alpha (last channel) is coded losslessly as one Modular extra channel appended after the colour
        // channels (the RCT only touches the 3 colour channels); the metadata declares one extra channel.
        int[]? alpha = null;
        if (image.HasAlpha)
        {
            int ai = srcCh - 1;
            alpha = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                var row = image.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    alpha[(y * w) + x] = Quantum.ScaleToDepth(row[(x * srcCh) + ai], bits);
                }
            }
        }

        // Grayscale (r == g == b everywhere) encodes as a single channel with no colour transform,
        // matching libjxl. For a small (single-group) grayscale image also try the RGB encoding and keep
        // whichever is smaller (a few-colour grey palette can occasionally beat the 1-channel form by a
        // byte); larger grayscale images just take the 1-channel form (skips a redundant full encode).
        // Alpha images take the colour (RGBA) path so the decoded result is [R,G,B,A]; grayscale-with-alpha
        // is rare and the extra colour channels cost little.
        Core.ColorspaceType colorspace = image.Colorspace;
        byte[]? icc = image.IccProfile;
        bool gray = alpha == null && IsGrayscale(r, g, b, w * h);
        if (!gray)
        {
            return EncodeCore(r, g, b, w, h, nb, gray: false, bits, alpha, colorspace, icc);
        }

        byte[] grayCs = EncodeCore(r, g, b, w, h, nb, gray: true, bits, alpha, colorspace, icc);
        if (w <= GroupDim && h <= GroupDim && alpha == null)
        {
            byte[] rgbCs = EncodeCore(r, g, b, w, h, nb, gray: false, bits, alpha, colorspace, icc);
            if (rgbCs.Length < grayCs.Length)
            {
                return rgbCs;
            }
        }

        return grayCs;
    }

    /// <summary>Encodes an animated image sequence as a single multi-frame lossless JPEG XL codestream
    /// (shared ImageMetadata with animation; one Modular frame per image with its per-frame duration).</summary>
    public static byte[] EncodeSequence(ImageSequence seq)
    {
        var frames = seq.Frames;
        if (frames.Count == 0)
        {
            throw new InvalidOperationException("Cannot encode an empty image sequence.");
        }

        if (frames.Count == 1)
        {
            return EncodeLossless(frames[0]); // a single frame is just a still image
        }

        int w = (int)frames[0].Columns, h = (int)frames[0].Rows;
        int numExtra = 0, bits = 8;
        foreach (ImageFrame f in frames)
        {
            if (f.HasAlpha)
            {
                numExtra = 1;
            }

            bits = Math.Max(bits, DetectBits(f));
        }

        var main = new JxlBitWriter();
        main.WriteBits(0xFF, 8);
        main.WriteBits(0x0A, 8);
        WriteSizeHeader(main, w, h);
        WriteImageMetadata(main, gray: false, bits, numExtra, animation: true, numLoops: (uint)Math.Max(0, seq.LoopCount));
        main.JumpToByteBoundary();

        for (int i = 0; i < frames.Count; i++)
        {
            ImageFrame f = frames[i];
            if ((int)f.Columns != w || (int)f.Rows != h)
            {
                throw new NotSupportedException("Animation frames must all match the canvas size.");
            }

            (int[] r, int[] g, int[] b, int[]? alpha) = ExtractRgbAlpha(f, bits, numExtra > 0);
            (byte[][] sections, int shift) = EncodeCoreSections(r, g, b, w, h, 3, gray: false, bits, alpha);
            uint duration = (uint)Math.Max(0, f.Delay); // tps = 100 => ticks are centiseconds
            WriteFrameBody(main, shift, sections, numExtra, isLast: i == frames.Count - 1, duration, animation: true);
        }

        return main.ToArray();
    }

    private static (int[] R, int[] G, int[] B, int[]? Alpha) ExtractRgbAlpha(ImageFrame image, int bits, bool needAlpha)
    {
        int w = (int)image.Columns, h = (int)image.Rows, srcCh = image.NumberOfChannels;
        int[] r = new int[w * h], g = new int[w * h], b = new int[w * h];
        int[]? alpha = needAlpha ? new int[w * h] : null;
        int maxV = (1 << bits) - 1;
        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * srcCh, p = (y * w) + x;
                if (srcCh == 1)
                {
                    r[p] = g[p] = b[p] = Quantum.ScaleToDepth(row[off], bits);
                }
                else
                {
                    r[p] = Quantum.ScaleToDepth(row[off], bits);
                    g[p] = Quantum.ScaleToDepth(row[off + 1], bits);
                    b[p] = Quantum.ScaleToDepth(row[off + 2], bits);
                }

                if (alpha != null)
                {
                    alpha[p] = image.HasAlpha ? Quantum.ScaleToDepth(row[off + srcCh - 1], bits) : maxV;
                }
            }
        }

        return (r, g, b, alpha);
    }

    private static byte[] EncodeCore(int[] r, int[] g, int[] b, int w, int h, int nb, bool gray, int bits = 8, int[]? alpha = null, Core.ColorspaceType colorspace = Core.ColorspaceType.SRGB, byte[]? icc = null)
    {
        var (sections, shift) = EncodeCoreSections(r, g, b, w, h, nb, gray, bits, alpha);
        return AssembleCodestream(w, h, shift, sections, gray, bits, alpha != null ? 1 : 0, colorspace, icc);
    }

    // Returns the frame's Modular sections + the group-size shift (the caller assembles them into a single-
    // image codestream or an animation frame body).
    private static (byte[][] Sections, int Shift) EncodeCoreSections(int[] r, int[] g, int[] b, int w, int h, int nb, bool gray, int bits = 8, int[]? alpha = null)
    {
        int numExtra = alpha != null ? 1 : 0;
        int nbCh = (gray ? 1 : nb) + numExtra;

        // Pick the reversible colour transform (none for grayscale), then the weighted-predictor parameter
        // mode. The WP-mode estimate (single-context) can mispredict once the tree/context model is
        // applied, so try the estimated-best mode and the default (mode 0) and keep the smaller.
        int rctType = gray ? -1 : ChooseRct(r, g, b, w, h);
        int[][] colorChan = gray ? new[] { r } : ForwardRct(r, g, b, w * h, rctType);
        // Append the alpha extra channel (untouched by the RCT, which only covers the 3 colour channels).
        int[][] chan = alpha == null ? colorChan : Append(colorChan, alpha);
        int wpEst = ChooseWpMode(chan, w, h);
        int[] wpModes = wpEst == 0 ? new[] { 0 } : new[] { wpEst, 0 };

        // A single group can cover an image up to GroupDim on both sides; larger images are tiled.
        bool single = w <= GroupDim && h <= GroupDim;
        if (!single)
        {
            byte[][] bestSecs = null!;
            int bestShift = GroupSizeShift;
            foreach (int gShift in GroupShiftCandidates(w, h))
            {
                int gd = 128 << gShift;
                foreach (int m in wpModes)
                {
                    byte[][] secs = BuildMultiGroupSections(chan, w, h, nbCh, gd, rctType, m);
                    if (bestSecs == null || TotalLength(secs) < TotalLength(bestSecs))
                    {
                        bestSecs = secs;
                        bestShift = gShift;
                    }
                }
            }

            return (bestSecs, bestShift);
        }

        int shift = SmallestShift(Math.Max(w, h));

        // Candidate A: the chosen RCT (or no transform) + WP mode. Candidate B (when the image has few
        // colours): the Palette transform. Keep whichever section is smaller.
        var rctChannels = new List<EncChannel>(nbCh);
        for (int c = 0; c < nbCh; c++)
        {
            rctChannels.Add(new EncChannel(chan[c], w, h));
        }

        byte[] best = null!;
        foreach (int m in wpModes)
        {
            byte[] sec = BuildSingleGroupSection(rctChannels, s => WriteRctTransform(s, rctType), m);
            if (best == null || sec.Length < best.Length)
            {
                best = sec;
            }
        }

        // Palette candidate: build the palette once, then try several colour orderings (by luma, by
        // frequency, and the top few that minimise the index's gradient-residual entropy) — each ordering
        // sets the index values and thus how well the gradient predictor tracks region boundaries. Score
        // them by an actual trial encode and keep the smallest, since the residual-entropy proxy only
        // ranks candidates; the tree/entropy coder decides the real winner.
        // Palette is only tried for 8-bit content: BuildPaletteChannels/the palette meta-channel path stores
        // entries at 8-bit precision, which would truncate a 16-bit palette (the high byte is lost). 16-bit
        // few-colour images still compress well via the RCT + weighted-predictor path above.
        if (alpha == null && bits <= 8 && CollectPaletteColors(r, g, b, w * h, out int[] colors, out int[] counts))
        {
            int nbColors = colors.Length;
            foreach (int[] ordered in PaletteOrderings(colors, counts, r, g, b, w, h))
            {
                List<EncChannel> palChannels = BuildPaletteChannels(ordered, r, g, b, w, h, gray);
                byte[] palSec = BuildSingleGroupSection(palChannels, s => WritePaletteTransform(s, nbColors, gray ? 1 : 3), 0);
                if (palSec.Length < best.Length)
                {
                    best = palSec;
                }
            }
        }

        return (new[] { best }, shift);
    }

    private static int[][] Append(int[][] chans, int[] extra)
    {
        var res = new int[chans.Length + 1][];
        Array.Copy(chans, res, chans.Length);
        res[chans.Length] = extra;
        return res;
    }

    private static bool IsGrayscale(int[] r, int[] g, int[] b, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (r[i] != g[i] || g[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A modular channel to encode: pixel data with its own dimensions.</summary>
    private readonly record struct EncChannel(int[] Data, int W, int H);

    private static byte[] AssembleCodestream(int w, int h, int shift, byte[][] sections, bool gray = false, int bits = 8, int numExtra = 0, Core.ColorspaceType colorspace = Core.ColorspaceType.SRGB, byte[]? icc = null)
    {
        var main = new JxlBitWriter();
        main.WriteBits(0xFF, 8);
        main.WriteBits(0x0A, 8);
        WriteSizeHeader(main, w, h);
        WriteImageMetadata(main, gray, bits, numExtra, colorspace: colorspace, icc: icc);
        main.JumpToByteBoundary();
        WriteFrameBody(main, shift, sections, numExtra, isLast: true, durationTicks: 0);
        return main.ToArray();
    }

    // One frame's body: frame header (with is_last / animation duration) + non-permuted TOC + sections.
    // Shared by the single-image codestream and the animation (multi-frame) codestream.
    private static void WriteFrameBody(JxlBitWriter main, int shift, byte[][] sections, int numExtra, bool isLast, uint durationTicks, bool animation = false)
    {
        WriteFrameHeader(main, shift, numExtra, isLast, durationTicks, animation);
        main.WriteBool(false); // permuted TOC = false
        main.JumpToByteBoundary();

        foreach (byte[] sec in sections)
        {
            main.WriteU32((uint)sec.Length, E.BitsOff(10, 0), E.BitsOff(14, 1024), E.BitsOff(22, 17408), E.BitsOff(30, 4211712));
        }

        main.JumpToByteBoundary();
        foreach (byte[] sec in sections)
        {
            main.AppendBytes(sec);
        }
    }

    // Group-size shifts to try for a tiled image: 1024px (shift 3, low per-group overhead) and 256px
    // (shift 1, many groups so the global tree can adapt per region via the group property).
    private static int[] GroupShiftCandidates(int w, int h) => new[] { 3 };

    private static int SmallestShift(int side)
    {
        int shift = 0;
        while ((128 << shift) < side && shift < GroupSizeShift)
        {
            shift++;
        }

        return shift;
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    private static void WriteSizeHeader(JxlBitWriter w, int width, int height)
    {
        w.WriteBool(false); // not "small"
        w.WriteU32((uint)height, E.BitsOff(9, 1), E.BitsOff(13, 1), E.BitsOff(18, 1), E.BitsOff(30, 1));
        w.WriteBits(0, 3); // aspect ratio 0 => explicit width
        w.WriteU32((uint)width, E.BitsOff(9, 1), E.BitsOff(13, 1), E.BitsOff(18, 1), E.BitsOff(30, 1));
    }

    private static void WriteImageMetadata(JxlBitWriter w, bool gray, int bits = 8, int numExtra = 0, bool animation = false, uint numLoops = 0, Core.ColorspaceType colorspace = Core.ColorspaceType.SRGB, byte[]? icc = null)
    {
        w.WriteBool(false); // not all_default
        w.WriteBool(animation); // extra_fields (only needed to carry animation)
        if (animation)
        {
            w.WriteBits(0, 3);   // orientation - 1 = 0
            w.WriteBool(false);  // have_intrinsic_size = false
            w.WriteBool(false);  // have_preview = false
            w.WriteBool(true);   // have_animation = true
            w.WriteU32(100, E.Val(100), E.Val(1000), E.BitsOff(10, 1), E.BitsOff(30, 1)); // tps_numerator (100 => ticks are centiseconds)
            w.WriteU32(1, E.Val(1), E.Val(1001), E.BitsOff(8, 1), E.BitsOff(10, 1));       // tps_denominator = 1
            w.WriteU32(numLoops, E.Val(0), E.BitsOff(3, 0), E.BitsOff(16, 0), E.BitsOff(32, 0)); // num_loops (0 = infinite)
            w.WriteBool(false);  // have_timecodes = false
        }

        w.WriteBool(false); // bit depth: not floating
        w.WriteU32((uint)bits, E.Val(8), E.Val(10), E.Val(12), E.BitsOff(6, 1)); // bits per sample
        // 16-bit samples with the RCT + predictors exceed the 16-bit modular range, so the decoder needs a
        // 32-bit working buffer; 8-bit content fits 16 bits.
        w.WriteBool(bits <= 8);
        w.WriteU32((uint)numExtra, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(12, 1)); // num_extra_channels
        for (int i = 0; i < numExtra; i++)
        {
            if (bits <= 8)
            {
                w.WriteBool(true); // ExtraChannelInfo: all_default (8-bit unassociated Alpha, dim_shift 0)
            }
            else
            {
                // Explicit alpha at the image bit depth (all_default alpha is 8-bit, which would mis-scale a
                // 16-bit alpha channel).
                w.WriteBool(false);  // not all_default
                w.WriteEnum(0);      // type = Alpha
                w.WriteBool(false);  // bit depth: not floating
                w.WriteU32((uint)bits, E.Val(8), E.Val(10), E.Val(12), E.BitsOff(6, 1)); // bits per sample
                w.WriteU32(0, E.Val(0), E.Val(3), E.Val(4), E.BitsOff(3, 1)); // dim_shift = 0
                w.WriteU32(0, E.Val(0), E.BitsOff(4, 0), E.BitsOff(5, 16), E.BitsOff(10, 48)); // name length = 0
                w.WriteBool(false);  // alpha_associated = false
            }
        }

        w.WriteBool(false); // xyb_encoded = false
        WriteColourEncoding(w, gray, colorspace, wantIcc: icc != null);

        if (animation)
        {
            w.WriteBool(true); // ToneMapping all_default (only present when extra_fields is set)
        }

        w.WriteU64(0);      // extensions = none
        w.WriteBool(true);  // default_m (skip opsin / upsampling weights)

        // The embedded ICC profile (if any) is written after the whole ImageMetadata bundle, before the frames.
        if (icc != null)
        {
            JxlIcc.EncodeStream(w, icc);
        }
    }

    // Writes the colour encoding for a lossless (non-XYB) frame. sRGB is all_default; grayscale and the
    // enum-representable wide-gamut spaces (Display P3, linear sRGB) are written explicitly so the pixels
    // (stored as-is, losslessly) are declared in the right space. TransferFunction kSRGB=13, kLinear=8;
    // WhitePoint kD65=1; Primaries kSRGB=1, kP3=11; ColorSpace kRGB=0, kGray=1; RenderingIntent kRelative=0.
    // Adobe RGB (1998) primaries (x,y) — white D65, encoding gamma 563/256.
    private static readonly double[] Adobe98Primaries = { 0.64, 0.33, 0.21, 0.71, 0.15, 0.06 };

    private static void WriteColourEncoding(JxlBitWriter w, bool gray, Core.ColorspaceType colorspace, bool wantIcc = false)
    {
        if (wantIcc)
        {
            // The embedded ICC profile is authoritative; only the colour-space kind is still declared (RGB /
            // Gray), the enum primaries/transfer/intent are omitted. The profile bytes follow the metadata.
            w.WriteBool(false);                 // not all_default
            w.WriteBool(true);                  // want_icc = true
            w.WriteEnum((uint)(gray ? 1 : 0));  // color_space (kGray / kRGB)
            return;
        }

        bool linear = colorspace == Core.ColorspaceType.LinearGray || colorspace == Core.ColorspaceType.ScRGB;
        bool adobe = colorspace == Core.ColorspaceType.Adobe98;
        int primaries = colorspace == Core.ColorspaceType.DisplayP3 ? 11 : (adobe ? 2 : 1); // kP3 / kCustom / kSRGB
        int transfer = linear ? 8 : 13; // kLinear else kSRGB

        // sRGB RGB with the sRGB transfer is exactly all_default.
        if (!gray && primaries == 1 && transfer == 13)
        {
            w.WriteBool(true); // all_default (sRGB RGB)
            return;
        }

        w.WriteBool(false); // not all_default
        w.WriteBool(false); // want_icc = false
        w.WriteEnum((uint)(gray ? 1 : 0));   // color_space (kGray / kRGB)
        w.WriteEnum(1);                       // white_point = kD65
        if (!gray)
        {
            w.WriteEnum((uint)primaries);     // primaries (only present for RGB)
            if (primaries == 2)               // kCustom => write the three (x,y) chromaticities
            {
                for (int i = 0; i < 6; i++)
                {
                    WriteCustomXy(w, Adobe98Primaries[i]);
                }
            }
        }

        if (adobe)
        {
            w.WriteBool(true); // have_gamma
            w.WriteBits((uint)Math.Round(1e7 / (563.0 / 256.0)), 24); // inverted encoding gamma (~4547069)
        }
        else
        {
            w.WriteBool(false);               // have_gamma = false
            w.WriteEnum((uint)transfer);      // transfer_function
        }

        w.WriteEnum(0);                       // rendering_intent
    }

    // Chromaticity coordinate: PackSigned(round(xy * 1e6)) via the Customxy U32 encoding.
    private static void WriteCustomXy(JxlBitWriter w, double xy)
    {
        int v = (int)Math.Round(xy * 1e6);
        uint packed = (uint)((v << 1) ^ (v >> 31)); // PackSigned (zigzag)
        w.WriteU32(packed, E.BitsOff(19, 0), E.BitsOff(19, 524288), E.BitsOff(20, 1048576), E.BitsOff(21, 2097152));
    }

    private static void WriteFrameHeader(JxlBitWriter w, int shift, int numExtra = 0, bool isLast = true, uint durationTicks = 0, bool animation = false)
    {
        w.WriteBool(false);  // not all_default
        w.WriteBits(0, 2);   // frame_type = Regular
        w.WriteBits(1, 1);   // encoding = Modular
        w.WriteU64(0);       // flags = 0
        w.WriteBool(false);  // do_ycbcr = false (non-XYB)
        w.WriteU32(1, E.Val(1), E.Val(2), E.Val(4), E.Val(8)); // upsampling = 1
        for (int i = 0; i < numExtra; i++)
        {
            w.WriteU32(1, E.Val(1), E.Val(2), E.Val(4), E.Val(8)); // ec_upsampling = 1
        }

        w.WriteBits((uint)shift, 2); // group_size_shift
        w.WriteU32(1, E.Val(1), E.Val(2), E.Val(3), E.BitsOff(3, 4)); // num_passes = 1
        w.WriteBool(false);  // have_crop = false
        w.WriteU32(0, E.Val(0), E.Val(1), E.Val(2), E.BitsOff(2, 3)); // color blending mode = 0 (replace)
        for (int i = 0; i < numExtra; i++)
        {
            w.WriteU32(0, E.Val(0), E.Val(1), E.Val(2), E.BitsOff(2, 3)); // extra-channel blending mode = 0
        }

        if (animation)
        {
            w.WriteU32(durationTicks, E.Val(0), E.Val(1), E.BitsOff(8, 0), E.BitsOff(32, 0)); // frame duration (ticks)
            // have_timecodes is false at the ImageMetadata level, so no timecode field here.
        }

        w.WriteBool(isLast); // is_last
        if (!isLast)
        {
            w.WriteBits(0, 2); // save_as_reference = 0
        }

        w.WriteU32(0, E.Val(0), E.BitsOff(4, 0), E.BitsOff(5, 16), E.BitsOff(10, 48)); // name length = 0

        // Loop filter: the all_default filter leaves Gaborish + EPF ON, which libjxl applies even to a
        // Modular frame and would blur a lossless image. Disable both explicitly.
        w.WriteBool(false);  // loop filter: not all_default
        w.WriteBool(false);  // gaborish = off
        w.WriteBits(0, 2);   // epf_iters = 0
        w.WriteU64(0);       // loop-filter extensions = none

        w.WriteU64(0);       // frame-header extensions = none
    }

    // --- Single-group frame: one section holding the tree, the pixel histogram and every pixel token
    // (DecodeGlobalModular). All channels are decoded through one entropy reader, so LZ77 spans them.
    // Builds the section with an error-context MA tree and with a plain single-leaf tree, keeping the
    // smaller — so context modelling is only used when it actually helps. ---
    private static byte[] BuildSingleGroupSection(List<EncChannel> channels, Action<JxlBitWriter> writeTransforms, int wpMode)
    {
        WpHeader wpHeader = WpMode(wpMode);
        List<EncChannelRef> refs = ToRefs(channels);
        byte[] best = null!;
        void Consider(LearnedTree tree)
        {
            byte[] pfx = BuildSection(channels, writeTransforms, tree, wpMode, useAns: false);
            if (best == null || pfx.Length < best.Length)
            {
                best = pfx;
            }

            byte[]? ansSec = BuildSection(channels, writeTransforms, tree, wpMode, useAns: true);
            if (ansSec != null && ansSec.Length < best.Length)
            {
                best = ansSec;
            }
        }

        Consider(SingleLeafTree);
        foreach (float threshold in JxlTreeLearner.NodeThresholds)
        {
            Consider(new LearnedTree(JxlTreeLearner.Learn(refs, wpHeader, threshold)));
        }

        return best;
    }

    private static byte[] BuildSection(List<EncChannel> channels, Action<JxlBitWriter> writeTransforms, LearnedTree tree, int wpMode, bool useAns = false)
    {
        WpHeader wpHeader = WpMode(wpMode);
        int total = 0;
        foreach (EncChannel ch in channels)
        {
            total += ch.W * ch.H;
        }

        int[] stream = new int[total];
        int[] ctxs = new int[total];
        int off = 0;
        int maxTok = 0;
        for (int c = 0; c < channels.Count; c++)
        {
            EncChannel ch = channels[c];
            ComputeResidualTokensCtx(ch, c, tree, stream, ctxs, off, ref maxTok, wpHeader, RefsFor(channels, c));
            off += ch.W * ch.H;
        }

        var streams = new List<(int[], int[])> { (stream, ctxs) };
        var plan = PlanPixelsCtx(streams, tree.LeafCount, out List<Op>[] ops, out _, useAns ? int.MaxValue / 2 : 0);

        JxlAnsWriter? ans = null;
        int[][] ansCounts = null!;
        if (useAns)
        {
            int logAlpha = Math.Max(5, JxlBits.CeilLog2(plan.LitAlphabet));
            if (logAlpha > 8)
            {
                return null!; // alphabet too large for ANS
            }

            ansCounts = new int[plan.K][];
            for (int c = 0; c < plan.K; c++)
            {
                ansCounts[c] = JxlEntropy.NormalizeCounts(plan.ClusterHist[c], JxlEntropy.HistShift);
            }

            ans = new JxlAnsWriter(ansCounts, logAlpha);
        }

        var s = new JxlBitWriter();
        s.WriteBits(1, 1); // DequantMatrices::DecodeDC all_default
        s.WriteBits(1, 1); // has_tree = 1
        WriteTree(s, tree.Tokens);
        if (useAns)
        {
            WriteAnsHistogramCtx(s, plan, ansCounts, ans!.LogAlpha);
        }
        else
        {
            WriteLz77HistogramCtx(s, plan);
        }

        s.WriteBool(true); // use_global tree + code
        WriteWpHeaderBits(s, wpMode);
        writeTransforms(s);
        if (useAns)
        {
            EmitOpsAns(s, ops[0], ctxs, plan, ans!);
        }
        else
        {
            EmitOpsCtx(s, ops[0], ctxs, plan);
        }

        return s.ToArray();
    }

    private static void WriteRctTransform(JxlBitWriter s, int rctType)
    {
        if (rctType < 0)
        {
            s.WriteU32(0, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(8, 18)); // num_transforms = 0 (grayscale: no colour transform)
            return;
        }

        s.WriteU32(1, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(8, 18)); // num_transforms = 1
        s.WriteU32(0, E.Val(0), E.Val(1), E.Val(2), E.Val(3)); // transform id = 0 (RCT)
        s.WriteU32(0, E.BitsOff(3, 0), E.BitsOff(6, 8), E.BitsOff(10, 72), E.BitsOff(13, 1096)); // begin_c = 0
        s.WriteU32((uint)rctType, E.Val(6), E.BitsOff(2, 0), E.BitsOff(4, 2), E.BitsOff(6, 10)); // rct_type
    }

    // Forward reversible colour transform: the exact inverse of JxlModular.InvRct for the given
    // rctType (perm * 7 + custom). Produces the three channels the decoder inverts back to RGB.
    private static int[][] ForwardRct(int[] r, int[] g, int[] b, int n, int rctType)
    {
        int[][] chan = { new int[n], new int[n], new int[n] };
        int perm = rctType / 7, custom = rctType % 7;
        int oi0 = perm % 3, oi1 = (perm + 1 + (perm / 3)) % 3, oi2 = (perm + 2 - (perm / 3)) % 3;
        int second = custom >> 1, third = custom & 1;
        for (int p = 0; p < n; p++)
        {
            int o0 = Pick(r, g, b, oi0, p), o1 = Pick(r, g, b, oi1, p), o2 = Pick(r, g, b, oi2, p);
            if (custom == 6)
            {
                int co = o0 - o2;
                int tmp = o2 + (co >> 1);
                int cg = o1 - tmp;
                chan[0][p] = tmp + (cg >> 1);
                chan[1][p] = co;
                chan[2][p] = cg;
            }
            else
            {
                chan[0][p] = o0;
                chan[1][p] = second == 1 ? o1 - o0 : (second == 2 ? o1 - ((o0 + o2) >> 1) : o1);
                chan[2][p] = o2 - (third != 0 ? o0 : 0);
            }
        }

        return chan;
    }

    private static int Pick(int[] r, int[] g, int[] b, int idx, int p) => idx == 0 ? r[p] : (idx == 1 ? g[p] : b[p]);

    // The five weighted-predictor parameter sets from libjxl (context_predict.h PredictorMode). Mode 0
    // equals WpHeader.Default().
    private static WpHeader WpMode(int m) => m switch
    {
        1 => new WpHeader { P1C = 8, P2C = 8, P3Ca = 4, P3Cb = 0, P3Cc = 3, P3Cd = 23, P3Ce = 2, W = new[] { 0xd, 0xc, 0xc, 0xb } },
        2 => new WpHeader { P1C = 10, P2C = 9, P3Ca = 7, P3Cb = 0, P3Cc = 0, P3Cd = 16, P3Ce = 9, W = new[] { 0xd, 0xc, 0xd, 0xc } },
        3 => new WpHeader { P1C = 16, P2C = 8, P3Ca = 0, P3Cb = 16, P3Cc = 0, P3Cd = 23, P3Ce = 0, W = new[] { 0xd, 0xd, 0xc, 0xc } },
        4 => new WpHeader { P1C = 10, P2C = 10, P3Ca = 5, P3Cb = 5, P3Cc = 5, P3Cd = 12, P3Ce = 4, W = new[] { 0xd, 0xc, 0xc, 0xc } },
        _ => WpHeader.Default(),
    };

    // Estimated bits for a WP mode: entropy of the weighted-predictor residuals over the channels.
    private static double EstimateWpModeCost(int[][] chan, int w, int h, int wpMode)
    {
        WpHeader hdr = WpMode(wpMode);
        var hist = new Dictionary<int, int>();
        long total = 0;
        var buf = new List<long>(1);
        foreach (int[] px in chan)
        {
            var wp = new WpState(hdr, w);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    long left = x > 0 ? px[(y * w) + x - 1] : (y > 0 ? px[((y - 1) * w) + x] : 0);
                    long top = y > 0 ? px[((y - 1) * w) + x] : left;
                    long topleft = (x > 0 && y > 0) ? px[((y - 1) * w) + x - 1] : left;
                    long topright = (x + 1 < w && y > 0) ? px[((y - 1) * w) + x + 1] : top;
                    long toptop = y > 1 ? px[((y - 2) * w) + x] : top;
                    buf.Clear();
                    long guess = wp.Predict(x, y, top, left, topright, topleft, toptop, buf);
                    int pixel = px[(y * w) + x];
                    int resid = pixel - (int)guess;
                    hist[resid] = hist.GetValueOrDefault(resid) + 1;
                    total++;
                    wp.Update(pixel, x, y);
                }
            }
        }

        if (total == 0)
        {
            return 0;
        }

        double bits = 0, invLog2 = 1.0 / Math.Log(2);
        foreach (int c in hist.Values)
        {
            bits -= c * Math.Log((double)c / total) * invLog2;
        }

        return bits;
    }

    private static int ChooseWpMode(int[][] chan, int w, int h)
    {
        int best = 0;
        double bestCost = double.MaxValue;
        for (int m = 0; m < 5; m++)
        {
            double c = EstimateWpModeCost(chan, w, h, m);
            if (c < bestCost)
            {
                bestCost = c;
                best = m;
            }
        }

        return best;
    }

    // Writes the weighted-predictor header: the compact "all default" bit for mode 0, else the explicit
    // 7 parameters + 4 weights (mirrors JxlModular.ReadWpHeader).
    private static void WriteWpHeaderBits(JxlBitWriter s, int wpMode)
    {
        if (wpMode == 0)
        {
            s.WriteBits(1, 1); // all default
            return;
        }

        WpHeader h = WpMode(wpMode);
        s.WriteBits(0, 1);
        s.WriteBits((uint)h.P1C, 5);
        s.WriteBits((uint)h.P2C, 5);
        s.WriteBits((uint)h.P3Ca, 5);
        s.WriteBits((uint)h.P3Cb, 5);
        s.WriteBits((uint)h.P3Cc, 5);
        s.WriteBits((uint)h.P3Cd, 5);
        s.WriteBits((uint)h.P3Ce, 5);
        for (int i = 0; i < 4; i++)
        {
            s.WriteBits((uint)h.W[i], 4);
        }
    }

    // Chooses the RCT with the lowest estimated cost: the summed entropy of each channel's clamped-
    // gradient residuals (sampled), which tracks the achievable size well and is cheap. Mirrors libjxl
    // trying all 42 RCTs and keeping the cheapest.
    private static int ChooseRct(int[] r, int[] g, int[] b, int w, int h)
    {
        int n = w * h;
        int bestType = 6;
        double bestCost = double.MaxValue;
        for (int rctType = 0; rctType < 42; rctType++)
        {
            int[][] chan = ForwardRct(r, g, b, n, rctType);
            double cost = GradientResidualBits(chan[0], w, h) + GradientResidualBits(chan[1], w, h) + GradientResidualBits(chan[2], w, h);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestType = rctType;
            }
        }

        return bestType;
    }

    // Entropy (bits) of a channel's clamped-gradient residuals over a sampled grid.
    private static double GradientResidualBits(int[] px, int w, int h)
    {
        int stride = Math.Max(1, (w * h) / (1 << 16));
        var hist = new Dictionary<int, int>();
        long total = 0;
        int cnt = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (cnt++ % stride != 0)
                {
                    continue;
                }

                int left = x > 0 ? px[(y * w) + x - 1] : (y > 0 ? px[((y - 1) * w) + x] : 0);
                int top = y > 0 ? px[((y - 1) * w) + x] : left;
                int topleft = (x > 0 && y > 0) ? px[((y - 1) * w) + x - 1] : left;
                int resid = px[(y * w) + x] - (int)JxlTreeLearner.ClampedGradient(left, top, topleft);
                hist[resid] = hist.GetValueOrDefault(resid) + 1;
                total++;
            }
        }

        if (total == 0)
        {
            return 0;
        }

        double bits = 0;
        double invLog2 = 1.0 / Math.Log(2);
        foreach (int c in hist.Values)
        {
            bits -= c * Math.Log((double)c / total) * invLog2;
        }

        return bits;
    }

    // Palette transform: replaces the three colour channels (begin 0, num 3) with a palette meta-channel
    // plus a single index channel. Inverted by JxlModular.InvPalette.
    private static void WritePaletteTransform(JxlBitWriter s, int nbColors, int numChannels)
    {
        s.WriteU32(1, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(8, 18)); // num_transforms = 1
        s.WriteU32(1, E.Val(0), E.Val(1), E.Val(2), E.Val(3)); // transform id = 1 (Palette)
        s.WriteU32(0, E.BitsOff(3, 0), E.BitsOff(6, 8), E.BitsOff(10, 72), E.BitsOff(13, 1096)); // begin_c = 0
        s.WriteU32((uint)numChannels, E.Val(1), E.Val(3), E.Val(4), E.BitsOff(13, 1)); // num_c (3 for RGB, 1 for grayscale)
        s.WriteU32((uint)nbColors, E.BitsOff(8, 0), E.BitsOff(10, 256), E.BitsOff(12, 1280), E.BitsOff(16, 5376)); // nb_colors
        s.WriteU32(0, E.Val(0), E.BitsOff(8, 1), E.BitsOff(10, 257), E.BitsOff(16, 1281)); // nb_deltas = 0
        s.WriteBits(0, 4); // predictor = 0 (palette entries stored directly, not delta-coded)
    }

    private const int MaxPaletteColors = 4096;
    private const int PaletteBruteForceMax = 7;   // brute-force ordering only up to this many colours
    private const int PaletteProxyTopK = 4;       // proxy-best orderings to trial-encode

    // Collects the distinct colours (and their pixel counts). Returns false if there are too many to palette.
    private static bool CollectPaletteColors(int[] r, int[] g, int[] b, int n, out int[] colors, out int[] counts)
    {
        colors = null!;
        counts = null!;
        var countByColor = new Dictionary<int, int>();
        foreach (int key in Keys(r, g, b, n))
        {
            countByColor[key] = countByColor.GetValueOrDefault(key) + 1;
            if (countByColor.Count > MaxPaletteColors)
            {
                return false;
            }
        }

        colors = new int[countByColor.Count];
        counts = new int[countByColor.Count];
        int i = 0;
        foreach (var kv in countByColor)
        {
            colors[i] = kv.Key;
            counts[i] = kv.Value;
            i++;
        }

        return true;
    }

    // Candidate palette entry orderings to trial-encode (see caller): by luma, by descending frequency,
    // and — for a small palette — the top-K orderings that minimise the index's gradient-residual entropy.
    // The order sets the index values and thus how well the gradient predictor tracks region boundaries;
    // the entropy proxy only ranks candidates, so the caller keeps whichever actually encodes smallest.
    private static List<int[]> PaletteOrderings(int[] colors, int[] counts, int[] r, int[] g, int[] b, int w, int h)
    {
        int nbColors = colors.Length;
        var byColor = new Dictionary<int, int>(nbColors);
        for (int k = 0; k < nbColors; k++)
        {
            byColor[colors[k]] = counts[k];
        }

        var result = new List<int[]>();

        int[] luma = (int[])colors.Clone();
        Array.Sort(luma, (a, c) => Luma(a).CompareTo(Luma(c)));
        result.Add(luma);

        int[] freq = (int[])colors.Clone();
        Array.Sort(freq, (a, c) => byColor[c].CompareTo(byColor[a]));
        result.Add(freq);

        if (nbColors <= PaletteBruteForceMax)
        {
            result.AddRange(TopPaletteOrders(colors, r, g, b, w, h));
        }

        return result;
    }

    private static List<EncChannel> BuildPaletteChannels(int[] orderedColors, int[] r, int[] g, int[] b, int w, int h, bool gray)
    {
        int nbColors = orderedColors.Length;
        var indexByColor = new Dictionary<int, int>(nbColors);
        for (int k = 0; k < nbColors; k++)
        {
            indexByColor[orderedColors[k]] = k;
        }

        int nc = gray ? 1 : 3;
        int[] palette = new int[nc * nbColors];
        for (int k = 0; k < nbColors; k++)
        {
            if (gray)
            {
                palette[k] = orderedColors[k] & 0xFF; // single grey row (r == g == b)
            }
            else
            {
                palette[k] = (orderedColors[k] >> 16) & 0xFF;              // R row
                palette[nbColors + k] = (orderedColors[k] >> 8) & 0xFF;    // G row
                palette[(2 * nbColors) + k] = orderedColors[k] & 0xFF;     // B row
            }
        }

        int n = w * h;
        int[] index = new int[n];
        for (int p = 0; p < n; p++)
        {
            index[p] = indexByColor[(r[p] << 16) | (g[p] << 8) | b[p]];
        }

        return new List<EncChannel>
        {
            new(palette, nbColors, nc), // palette meta-channel decoded first
            new(index, w, h),           // then the index channel
        };
    }

    // The PaletteProxyTopK orderings (as ordered-colour arrays) with the lowest index gradient-residual
    // entropy, brute-forced over permutations. The residual of a pixel depends only on the base indices of
    // (self, left, top, topleft), so orderings are scored over the collapsed distinct 4-tuples, not pixels.
    private static List<int[]> TopPaletteOrders(int[] colors, int[] r, int[] g, int[] b, int w, int h)
    {
        int nbColors = colors.Length;
        int n = w * h;
        var idxOf = new Dictionary<int, int>(nbColors);
        for (int k = 0; k < nbColors; k++)
        {
            idxOf[colors[k]] = k;
        }

        int[] baseIdx = new int[n];
        for (int p = 0; p < n; p++)
        {
            baseIdx[p] = idxOf[(r[p] << 16) | (g[p] << 8) | b[p]];
        }

        var tupleCounts = new Dictionary<int, int>();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w) + x;
                int self = baseIdx[p];
                // 7 marks the literal-0 neighbour fallback (the (0,0) corner), mapped to permuted index 0
                // in the scorer — exactly matching JxlTreeLearner.ComputePixel's boundary handling.
                int left = x > 0 ? baseIdx[p - 1] : (y > 0 ? baseIdx[p - w] : 7);
                int top = y > 0 ? baseIdx[p - w] : left;
                int topleft = (x > 0 && y > 0) ? baseIdx[p - w - 1] : left;
                int key = self | (left << 3) | (top << 6) | (topleft << 9);
                tupleCounts[key] = tupleCounts.GetValueOrDefault(key) + 1;
            }
        }

        int[] tKeys = new int[tupleCounts.Count];
        int[] tCnt = new int[tupleCounts.Count];
        int ti = 0;
        foreach (var kv in tupleCounts)
        {
            tKeys[ti] = kv.Key;
            tCnt[ti] = kv.Value;
            ti++;
        }

        double[] topCost = new double[PaletteProxyTopK];
        int[][] topPerm = new int[PaletteProxyTopK][];
        for (int i = 0; i < PaletteProxyTopK; i++)
        {
            topCost[i] = double.MaxValue;
        }

        int[] perm = new int[nbColors];
        for (int k = 0; k < nbColors; k++)
        {
            perm[k] = k;
        }

        int[] counts = new int[(2 * nbColors) + 1];
        void Eval()
        {
            double c = OrderResidualBits(tKeys, tCnt, perm, n, counts);
            int worst = 0;
            for (int i = 1; i < PaletteProxyTopK; i++)
            {
                if (topCost[i] > topCost[worst])
                {
                    worst = i;
                }
            }

            if (c < topCost[worst])
            {
                topCost[worst] = c;
                topPerm[worst] = (int[])perm.Clone();
            }
        }

        int[] stack = new int[nbColors];
        Eval();
        for (int iHeap = 0; iHeap < nbColors;)
        {
            if (stack[iHeap] < iHeap)
            {
                int a = (iHeap & 1) == 0 ? 0 : stack[iHeap];
                (perm[a], perm[iHeap]) = (perm[iHeap], perm[a]);
                Eval();
                stack[iHeap]++;
                iHeap = 0;
            }
            else
            {
                stack[iHeap] = 0;
                iHeap++;
            }
        }

        var result = new List<int[]>();
        foreach (int[] tp in topPerm)
        {
            if (tp == null)
            {
                continue;
            }

            int[] ordered = new int[nbColors];
            for (int k = 0; k < nbColors; k++)
            {
                ordered[tp[k]] = colors[k];
            }

            result.Add(ordered);
        }

        return result;
    }

    // Entropy (bits) of the clamped-gradient residuals of the index field under a given colour ordering,
    // computed over the collapsed 4-tuple counts.
    private static double OrderResidualBits(int[] tKeys, int[] tCnt, int[] perm, int total, int[] counts)
    {
        Array.Clear(counts);
        int off = counts.Length / 2;
        for (int i = 0; i < tKeys.Length; i++)
        {
            int key = tKeys[i];
            int self = perm[key & 7];
            int lk = (key >> 3) & 7, tk = (key >> 6) & 7, tlk = (key >> 9) & 7;
            int left = lk == 7 ? 0 : perm[lk];
            int top = tk == 7 ? 0 : perm[tk];
            int topleft = tlk == 7 ? 0 : perm[tlk];
            int resid = self - (int)JxlTreeLearner.ClampedGradient(left, top, topleft);
            counts[resid + off] += tCnt[i];
        }

        double bits = 0;
        double invLog2 = 1.0 / Math.Log(2);
        foreach (int c in counts)
        {
            if (c > 0)
            {
                bits -= c * Math.Log((double)c / total) * invLog2;
            }
        }

        return bits;
    }

    private static IEnumerable<int> Keys(int[] r, int[] g, int[] b, int n)
    {
        for (int p = 0; p < n; p++)
        {
            yield return (r[p] << 16) | (g[p] << 8) | b[p];
        }
    }

    private static int Luma(int rgb) => (((rgb >> 16) & 0xFF) * 2) + (((rgb >> 8) & 0xFF) * 5) + (rgb & 0xFF);

    // --- Multi-group frame: LfGlobal (tree + pixel histogram + global RCT), empty LfGroup/HfGlobal
    // sections, then one Modular-AC section per spatial group. All groups share the global tree and
    // codes; each group has its own entropy reader/window, so LZ77 and prediction run per group. Built
    // with an error-context tree and with a single-leaf tree, keeping whichever total is smaller. ---
    private static byte[][] BuildMultiGroupSections(int[][] chan, int w, int h, int nb, int groupDim, int rctType, int wpMode)
    {
        // Learn the tree from the actual tiles (per-tile prediction) so it matches the per-group encode.
        var tileRefs = new List<EncChannelRef>();
        int gprL = CeilDiv(w, groupDim);
        int numG = gprL * CeilDiv(h, groupDim);
        int lfDimL = groupDim * 8;
        int numLfL = CeilDiv(w, lfDimL) * CeilDiv(h, lfDimL);
        for (int g = 0; g < numG; g++)
        {
            int rx = (g % gprL) * groupDim, ry = (g / gprL) * groupDim;
            int rw = Math.Min(groupDim, w - rx), rh = Math.Min(groupDim, h - ry);
            int streamId = 1 + (3 * numLfL) + 17 + g; // matches BuildMultiGroupWithTree / the decoder
            var tiles = new int[nb][];
            for (int c = 0; c < nb; c++)
            {
                tiles[c] = ExtractTile(chan[c], w, rx, ry, rw, rh);
            }

            for (int c = 0; c < nb; c++)
            {
                tileRefs.Add(new EncChannelRef(tiles[c], rw, rh, c, streamId, TileRefs(tiles, c)));
            }
        }

        // For each candidate tree, try both the prefix+LZ77 and the ANS (no-LZ77) entropy coder and keep
        // whichever section set is smaller — best-per-image, so neither coder can ever regress a result.
        void Consider(LearnedTree t, ref byte[][] bestSecs)
        {
            var streams = ComputeGroupStreams(chan, w, h, nb, groupDim, t, WpMode(wpMode));
            byte[][] pfx = BuildMultiGroupWithTree(chan, w, h, nb, groupDim, t, rctType, wpMode, useAns: false, streams);
            if (bestSecs == null || TotalLength(pfx) < TotalLength(bestSecs))
            {
                bestSecs = pfx;
            }

            byte[][] ansSecs = BuildMultiGroupWithTree(chan, w, h, nb, groupDim, t, rctType, wpMode, useAns: true, streams);
            if (ansSecs != null && TotalLength(ansSecs) < TotalLength(bestSecs))
            {
                bestSecs = ansSecs;
            }
        }

        byte[][] best = null!;
        Consider(SingleLeafTree, ref best);
        foreach (float threshold in JxlTreeLearner.NodeThresholds)
        {
            var learned = new LearnedTree(JxlTreeLearner.Learn(tileRefs, WpMode(wpMode), threshold));
            Consider(learned, ref best);
        }

        return best;
    }

    // Reference channels for a same-size tile set: earlier channels (c-1, c-2, ...), up to MaxRefChannels.
    private static int[][] TileRefs(int[][] tiles, int c)
    {
        int n = Math.Min(c, JxlTreeLearner.MaxRefChannels);
        int[][] refs = new int[n][];
        for (int k = 0; k < n; k++)
        {
            refs[k] = tiles[c - 1 - k];
        }

        return refs;
    }

    private static int TotalLength(byte[][] sections)
    {
        int t = 0;
        foreach (byte[] s in sections)
        {
            t += s.Length;
        }

        return t;
    }

    // The per-group residual/context streams for a tree+WP mode. Shared between the prefix and ANS coder
    // attempts so the (expensive) weighted-predictor pass runs once per candidate, not once per coder.
    private static List<(int[] Stream, int[] Ctxs)> ComputeGroupStreams(int[][] chan, int w, int h, int nb, int groupDim, LearnedTree tree, WpHeader wpHeader)
    {
        int gpr = CeilDiv(w, groupDim);
        int numGroups = gpr * CeilDiv(h, groupDim);
        int lfDim = groupDim * 8;
        int numLf = CeilDiv(w, lfDim) * CeilDiv(h, lfDim);

        var streams = new List<(int[] Stream, int[] Ctxs)>(numGroups);
        for (int g = 0; g < numGroups; g++)
        {
            int rx = (g % gpr) * groupDim;
            int ry = (g / gpr) * groupDim;
            int rw = Math.Min(groupDim, w - rx);
            int rh = Math.Min(groupDim, h - ry);

            // Each group predicts within its own tile: extract the tile of every channel and treat it as
            // a standalone image (identical to the decoder's per-group decode).
            int[] stream = new int[rw * rh * nb];
            int[] ctxs = new int[rw * rh * nb];
            var tiles = new int[nb][];
            for (int c = 0; c < nb; c++)
            {
                tiles[c] = ExtractTile(chan[c], w, rx, ry, rw, rh);
            }

            int off = 0;
            int maxTok = 0;
            int streamId = 1 + (3 * numLf) + 17 + g; // ModularStreamId::ModularAC (matches the decoder)
            for (int c = 0; c < nb; c++)
            {
                ComputeResidualTokensCtx(new EncChannel(tiles[c], rw, rh), c, tree, stream, ctxs, off, ref maxTok, wpHeader, TileRefs(tiles, c), streamId);
                off += rw * rh;
            }

            streams.Add((stream, ctxs));
        }

        return streams;
    }

    private static byte[][] BuildMultiGroupWithTree(int[][] chan, int w, int h, int nb, int groupDim, LearnedTree tree, int rctType, int wpMode, bool useAns = false, List<(int[] Stream, int[] Ctxs)>? precomputedStreams = null)
    {
        WpHeader wpHeader = WpMode(wpMode);
        int gpr = CeilDiv(w, groupDim);
        int numGroups = gpr * CeilDiv(h, groupDim);
        int lfDim = groupDim * 8;
        int numLf = CeilDiv(w, lfDim) * CeilDiv(h, lfDim);

        var streams = precomputedStreams ?? ComputeGroupStreams(chan, w, h, nb, groupDim, tree, wpHeader);

        // ANS mode encodes without LZ77 (photographic residual distributions are skewed, so ANS coding at
        // fractional bits beats prefix+LZ77). Fall back to the prefix path if the alphabet is too large.
        JxlAnsWriter? ans = null;
        int[][] ansCounts = null!;
        var plan = PlanPixelsCtx(streams, tree.LeafCount, out List<Op>[] ops, out _, useAns ? int.MaxValue / 2 : 0);
        if (useAns)
        {
            int logAlpha = Math.Max(5, JxlBits.CeilLog2(plan.LitAlphabet));
            if (logAlpha > 8)
            {
                return null!; // alphabet too large for ANS (max 256 symbols)
            }

            ansCounts = new int[plan.K][];
            for (int c = 0; c < plan.K; c++)
            {
                ansCounts[c] = JxlEntropy.NormalizeCounts(plan.ClusterHist[c], JxlEntropy.HistShift);
            }

            ans = new JxlAnsWriter(ansCounts, logAlpha);
        }

        // Section 0: LfGlobal.
        var s0 = new JxlBitWriter();
        s0.WriteBits(1, 1); // DequantMatrices::DecodeDC all_default
        s0.WriteBits(1, 1); // has_tree = 1
        WriteTree(s0, tree.Tokens);
        if (useAns)
        {
            WriteAnsHistogramCtx(s0, plan, ansCounts, ans!.LogAlpha);
        }
        else
        {
            WriteLz77HistogramCtx(s0, plan);
        }

        WriteGlobalGroupHeader(s0, rctType, wpMode); // use_global + wp + RCT; no channels are small enough to decode here

        // The global-modular stream decodes zero channels here (all are group-sized), but an ANS decoder
        // still reads its initial 32-bit state before finding no channels — so emit the empty-stream state
        // (the ANS signature). Prefix codes carry no state, so nothing is written for them.
        if (useAns)
        {
            s0.WriteBits((uint)JxlBits.AnsSignature << 16, 32);
        }

        byte[] s0bytes = s0.ToArray();

        var list = new List<byte[]>(1 + numLf + 1 + numGroups) { s0bytes };
        for (int i = 0; i < numLf; i++)
        {
            list.Add(Array.Empty<byte>()); // LfGroup: empty for a Modular frame
        }

        list.Add(Array.Empty<byte>()); // HfGlobal: empty for a Modular frame

        for (int g = 0; g < numGroups; g++)
        {
            var sg = new JxlBitWriter();
            sg.WriteBool(true); // use_global tree + code
            WriteWpHeaderBits(sg, wpMode);
            sg.WriteU32(0, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(8, 18)); // num_transforms = 0
            if (useAns)
            {
                EmitOpsAns(sg, ops[g], streams[g].Ctxs, plan, ans!);
                // The ANS decoder peeks 16 bits ahead even for the final symbol; leave a few slack bytes
                // inside the section so a strict decoder's look-ahead never runs off the section end.
                // (slack removed)
            }
            else
            {
                EmitOpsCtx(sg, ops[g], streams[g].Ctxs, plan);
            }

            list.Add(sg.ToArray());
        }

        return list.ToArray();
    }

    private static int[] ExtractTile(int[] full, int fullW, int rx, int ry, int rw, int rh)
    {
        int[] tile = new int[rw * rh];
        for (int y = 0; y < rh; y++)
        {
            Array.Copy(full, ((ry + y) * fullW) + rx, tile, y * rw, rw);
        }

        return tile;
    }

    // Single-leaf global MA tree: property token 0 (=> leaf), then predictor / offset / mult-log /
    // mult-bits. The tree stream reads six contexts, all mapped to one histogram.
    // ─── LZ77 over the residual-token stream ───────────────────────────────────────────────────────
    // The Modular pixel stream is entropy-coded with LZ77 enabled: a token >= a threshold signals a
    // back-reference (length + distance) instead of a literal residual, which collapses flat and
    // repeating regions. Distances go in their own histogram cluster; literals + length markers use the
    // per-context clusters — matching DecodeHistograms + JxlAnsReader's LZ77 path.
    private readonly struct Op
    {
        public readonly bool Match;
        public readonly int A; // literal value, or match length
        public readonly int B; // match distance
        public Op(bool match, int a, int b) { Match = match; A = a; B = b; }
    }

    private const int Lz77MinLength = 16;   // shortest worthwhile back-reference
    private const int NumSpecialDistances = 120; // JxlEntropy.NumSpecialDistances (distanceMultiplier > 0)

    // Greedy longest-match LZ77 over the value stream, using a 4-gram hash with chained positions.
    // Overlapping matches (distance < length) are allowed — they reproduce run-length fills.
    private static List<Op> FindMatches(int[] v, int minLen)
    {
        var ops = new List<Op>();
        int n = v.Length;

        // LZ77 disabled (ANS path uses a huge minLen): skip the match search entirely and emit literals.
        if (minLen > n)
        {
            for (int i = 0; i < n; i++)
            {
                ops.Add(new Op(false, v[i], 0));
            }

            return ops;
        }

        const int WindowMask = (1 << 20) - 1;
        var head = new Dictionary<int, int>();
        int[] prev = new int[Math.Max(1, n)];

        int Hash(int i)
        {
            unchecked
            {
                uint hh = (uint)v[i];
                hh = (hh * 2654435761u) + (uint)v[i + 1];
                hh = (hh * 2654435761u) + (uint)v[i + 2];
                hh = (hh * 2654435761u) + (uint)v[i + 3];
                return (int)(hh & 0x7FFFFFFF);
            }
        }

        void Insert(int i)
        {
            if (i + 4 > n)
            {
                return;
            }

            int hh = Hash(i);
            prev[i] = head.TryGetValue(hh, out int p) ? p : -1;
            head[hh] = i;
        }

        int idx = 0;
        while (idx < n)
        {
            int bestLen = 0, bestDist = 0;
            if (idx + 4 <= n && head.TryGetValue(Hash(idx), out int p))
            {
                int tries = 96;
                while (p >= 0 && tries-- > 0)
                {
                    int dist = idx - p;
                    if (dist > (WindowMask + 1))
                    {
                        break;
                    }

                    int maxl = n - idx;
                    int l = 0;
                    while (l < maxl && v[p + l] == v[idx + l])
                    {
                        l++;
                    }

                    if (l > bestLen)
                    {
                        bestLen = l;
                        bestDist = dist;
                    }

                    p = prev[p];
                }
            }

            if (bestLen >= minLen)
            {
                ops.Add(new Op(true, bestLen, bestDist));
                int end = idx + bestLen;
                for (int j = idx; j < end; j++)
                {
                    Insert(j);
                }

                idx = end;
            }
            else
            {
                ops.Add(new Op(false, v[idx], 0));
                Insert(idx);
                idx++;
            }
        }

        return ops;
    }

    // Encodes a value under a hybrid-uint config (SplitExp, 0, 0): values below 2^SplitExp are literal
    // tokens; larger values become an exponent token plus mantissa extra bits. Inverse of
    // JxlEntropy.ReadHybridUintConfig.
    private static (int Token, int NBits, int Bits) PackHybridUint(int splitExp, int value)
    {
        int splitToken = 1 << splitExp;
        if (value < splitToken)
        {
            return (value, 0, 0);
        }

        int nb = BitLen(value) - 1;      // floor(log2(value))
        int token = splitToken + (nb - splitExp);
        int bits = value - (1 << nb);
        return (token, nb, bits);
    }

    private static int BitLen(int x) => x <= 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)x);

    // Literals are tokenised with libjxl's compact hybrid-uint config (split 4, msb 1, lsb 2): small
    // token alphabet (so histogram headers stay small) plus raw mantissa bits.
    private const int LitSplit = 4, LitMsb = 1, LitLsb = 2;

    // General hybrid-uint encode (inverse of JxlEntropy.ReadHybridUintConfig), ported from libjxl
    // HybridUintConfig::Encode. Handles msb/lsb, unlike the (splitExp,0,0)-only PackHybridUint above.
    private static (int Token, int NBits, int Bits) PackHybridFull(int splitExp, int msb, int lsb, int value)
    {
        int splitToken = 1 << splitExp;
        if (value < splitToken)
        {
            return (value, 0, 0);
        }

        int n = BitLen(value) - 1; // floor(log2)
        int m = value - (1 << n);
        int token = splitToken + ((n - splitExp) << (msb + lsb)) + ((m >> (n - msb)) << lsb) + (m & ((1 << lsb) - 1));
        int nbits = n - msb - lsb;
        int bits = (value >> lsb) & ((1 << nbits) - 1);
        return (token, nbits, bits);
    }

    // ─── Context modelling ─────────────────────────────────────────────────────────────────────────
    // A small MA tree assigns each pixel a context from the weighted predictor's error property (the
    // decoder property 15 — the neighbour with the largest recent WP error). High-error (busy) pixels
    // and low-error (flat) pixels get separate entropy statistics. The tree is emitted so the decoder
    // computes the identical context; the encoder walks the same tree on the same property value.
    private const int MaxLiteralClusters = 128;

    // Wraps a learned MA tree (JxlTreeLearner): serialises it in the decoder's breadth-first order,
    // numbering leaves as contexts, and walks it per pixel to pick the leaf (context + predictor).
    private sealed class LearnedTree
    {
        public readonly MaTreeNode Root;
        public readonly int[] Tokens;
        public readonly int LeafCount;

        public LearnedTree(MaTreeNode root)
        {
            Root = root;
            var tokens = new List<int>();
            var queue = new Queue<MaTreeNode>();
            queue.Enqueue(root);
            int leaf = 0;
            while (queue.Count > 0)
            {
                MaTreeNode node = queue.Dequeue();
                if (node.Property == -1)
                {
                    node.Ctx = leaf++;
                    tokens.Add(0);                 // property 0 => leaf
                    tokens.Add(node.Predictor);    // predictor
                    tokens.Add(0);                 // offset (PackSigned 0)
                    tokens.Add(0);                 // mult-log
                    tokens.Add(0);                 // mult-bits
                }
                else
                {
                    tokens.Add(node.Property + 1);          // property token
                    tokens.Add(PackSigned(node.SplitVal));  // split value
                    queue.Enqueue(node.Left!);              // property > SplitVal
                    queue.Enqueue(node.Right!);             // property <= SplitVal
                }
            }

            Tokens = tokens.ToArray();
            LeafCount = leaf;
        }

        public MaTreeNode Walk(int[] props)
        {
            MaTreeNode node = Root;
            while (node.Property != -1)
            {
                node = props[node.Property] > node.SplitVal ? node.Left! : node.Right!;
            }

            return node;
        }
    }

    private static readonly LearnedTree SingleLeafTree = new(new MaTreeNode { Property = -1, Predictor = WeightedPredictor });

    private static List<EncChannelRef> ToRefs(List<EncChannel> channels)
    {
        var refs = new List<EncChannelRef>(channels.Count);
        for (int c = 0; c < channels.Count; c++)
        {
            refs.Add(new EncChannelRef(channels[c].Data, channels[c].W, channels[c].H, c, 0, RefsFor(channels, c)));
        }

        return refs;
    }

    // Reference channel data for channel c: earlier same-size channels (c-1, c-2, ...), most recent
    // first, up to MaxRefChannels — matching JxlModular.DecodeChannel's reference selection.
    private static int[][] RefsFor(List<EncChannel> channels, int c)
    {
        var list = new List<int[]>();
        EncChannel ch = channels[c];
        for (int j = c - 1; j >= 0 && list.Count < JxlTreeLearner.MaxRefChannels; j--)
        {
            if (channels[j].W == ch.W && channels[j].H == ch.H)
            {
                list.Add(channels[j].Data);
            }
        }

        return list.ToArray();
    }

    private static int PackSigned(int v) => (int)(uint)((v << 1) ^ (v >> 31));

    private static void WriteTree(JxlBitWriter s, int[] tokens)
    {
        int maxv = 0;
        foreach (int v in tokens)
        {
            if (v > maxv)
            {
                maxv = v;
            }
        }

        long[] f = new long[maxv + 1];
        foreach (int v in tokens)
        {
            f[v]++;
        }

        var treeCode = new JxlPrefixCode(f, maxv + 1);
        WriteHistogramsHeader(s, numContexts: 6, treeCode);
        foreach (int v in tokens)
        {
            treeCode.WriteSymbol(s, v);
        }
    }

    private sealed class PixelPlanCtx
    {
        public int N;                     // number of pixel contexts (tree leaves)
        public int K;                     // number of literal clusters
        public int Threshold;
        public int SeDist, SeLen, MinLen;
        public int[] ContextToCluster = null!; // [N] pixel context -> literal cluster (0..K-1)
        public JxlPrefixCode[] Codes = null!;  // [0..K-1] literal+length clusters, [K] distance
        public long[][] ClusterHist = null!;   // [0..K-1] raw literal+length counts (for ANS normalization)
        public int LitAlphabet;                // literal+length token alphabet size (gmaxLit + 1)
    }

    // Plans the entropy coding for one or more token streams that share the global tree and codes
    // (one stream for a single-group frame, one per group otherwise). Returns per-stream LZ77 ops.
    private static PixelPlanCtx PlanPixelsCtx(List<(int[] Stream, int[] Ctxs)> streams, int n, out List<Op>[] opsOut, out long[][] rawCtxHist, int minLenOverride = 0)
    {
        int minLen = minLenOverride > 0 ? minLenOverride : Lz77MinLength;
        opsOut = new List<Op>[streams.Count];
        for (int i = 0; i < streams.Count; i++)
        {
            opsOut[i] = FindMatches(streams[i].Stream, minLen);
        }

        // Literal tokens are compact (config LitSplit/LitMsb/LitLsb); the match threshold sits just above
        // the largest literal token so length markers never collide with literals.
        int maxLitTok = 0;
        foreach (List<Op> ops in opsOut)
        {
            foreach (Op op in ops)
            {
                if (!op.Match)
                {
                    maxLitTok = Math.Max(maxLitTok, PackHybridFull(LitSplit, LitMsb, LitLsb, op.A).Token);
                }
            }
        }

        var plan = new PixelPlanCtx
        {
            N = n,
            Threshold = Math.Max(8, maxLitTok + 1),
            SeDist = 4,
            SeLen = 4,
            MinLen = minLen,
        };

        int gmaxLit = maxLitTok, gmaxDist = 0;
        foreach (List<Op> ops in opsOut)
        {
            foreach (Op op in ops)
            {
                if (op.Match)
                {
                    gmaxLit = Math.Max(gmaxLit, plan.Threshold + PackHybridUint(plan.SeLen, op.A - minLen).Token);
                    gmaxDist = Math.Max(gmaxDist, PackHybridUint(plan.SeDist, op.B + NumSpecialDistances - 1).Token);
                }
            }
        }

        // Per-context literal+length histograms (global across streams), plus one shared distance histogram.
        long[][] ctxHist = new long[n][];
        for (int c = 0; c < n; c++)
        {
            ctxHist[c] = new long[gmaxLit + 1];
        }

        long[] distHist = new long[gmaxDist + 1];
        for (int i = 0; i < streams.Count; i++)
        {
            int[] ctxs = streams[i].Ctxs;
            int pos = 0;
            foreach (Op op in opsOut[i])
            {
                int ctx = ctxs[pos];
                if (!op.Match)
                {
                    ctxHist[ctx][PackHybridFull(LitSplit, LitMsb, LitLsb, op.A).Token]++;
                    pos += 1;
                }
                else
                {
                    ctxHist[ctx][plan.Threshold + PackHybridUint(plan.SeLen, op.A - minLen).Token]++;
                    distHist[PackHybridUint(plan.SeDist, op.B + NumSpecialDistances - 1).Token]++;
                    pos += op.A;
                }
            }
        }

        rawCtxHist = ctxHist;
        int[] contextToCluster = ClusterContexts(ctxHist, gmaxLit + 1, MaxLiteralClusters, out long[][] clusterHist, out int k);
        plan.K = k;
        plan.ContextToCluster = contextToCluster;
        plan.ClusterHist = clusterHist;
        plan.LitAlphabet = gmaxLit + 1;
        plan.Codes = new JxlPrefixCode[k + 1];
        for (int c = 0; c < k; c++)
        {
            int alpha = 1;
            for (int sym = clusterHist[c].Length - 1; sym >= 0; sym--)
            {
                if (clusterHist[c][sym] > 0)
                {
                    alpha = sym + 1;
                    break;
                }
            }

            plan.Codes[c] = new JxlPrefixCode(clusterHist[c], alpha);
        }

        plan.Codes[k] = new JxlPrefixCode(distHist, gmaxDist + 1);

        return plan;
    }

    // Seed-based histogram clustering ported from libjxl (enc_cluster.cc FastClusterHistograms): seed
    // the largest histogram, then repeatedly seed the histogram farthest (by merge cost) from all
    // current seeds, until none is more than kMinDistanceForDistinct bits distinct or the cap is hit;
    // finally assign every context to its nearest seed. Picks the natural number of histograms.
    private const double MinDistanceForDistinct = 48.0;

    private static int[] ClusterContexts(long[][] ctxHist, int alphabet, int maxHistograms, out long[][] clusterHist, out int k)
        => ClusterContexts(ctxHist, alphabet, maxHistograms, MinDistanceForDistinct, out clusterHist, out k);

    // minDistance = the coding-bits gain a new histogram must beat before it is worth adding. It should
    // cover the histogram's own header cost, else clustering over-splits and the header (HfGlobal) bloats —
    // dominant on small images at coarse quant. The lossless path keeps the libjxl-matching 48.
    private static int[] ClusterContexts(long[][] ctxHist, int alphabet, int maxHistograms, double minDistance, out long[][] clusterHist, out int k)
    {
        int n = ctxHist.Length;
        double[] entropy = new double[n];
        long[] total = new long[n];
        double[] dist = new double[n];
        int[] symbol = new int[n];
        int largest = 0;
        for (int i = 0; i < n; i++)
        {
            symbol[i] = -1;
            dist[i] = double.MaxValue;
            foreach (long v in ctxHist[i])
            {
                total[i] += v;
            }

            if (total[i] == 0)
            {
                symbol[i] = 0; // empty contexts fold into cluster 0
                dist[i] = 0;
                continue;
            }

            entropy[i] = Bits(ctxHist[i]);
            if (total[i] > total[largest])
            {
                largest = i;
            }
        }

        var seeds = new List<long[]>();
        while (seeds.Count < maxHistograms)
        {
            symbol[largest] = seeds.Count;
            seeds.Add((long[])ctxHist[largest].Clone());
            dist[largest] = 0;
            double seedEntropy = Bits(seeds[^1]);
            largest = 0;
            for (int i = 0; i < n; i++)
            {
                if (dist[i] == 0)
                {
                    continue;
                }

                double d = Bits(Sum(ctxHist[i], seeds[^1])) - entropy[i] - seedEntropy;
                if (d < dist[i])
                {
                    dist[i] = d;
                }

                if (dist[i] > dist[largest])
                {
                    largest = i;
                }
            }

            if (dist[largest] < minDistance)
            {
                break;
            }
        }

        if (seeds.Count == 0)
        {
            seeds.Add(new long[alphabet]); // degenerate: all contexts empty
        }

        // Assign each still-unassigned context to its nearest seed and accumulate (seeds and empty
        // contexts already have a symbol and are skipped).
        for (int i = 0; i < n; i++)
        {
            if (symbol[i] != -1)
            {
                continue;
            }

            int best = 0;
            double bestDist = double.MaxValue;
            for (int j = 0; j < seeds.Count; j++)
            {
                double d = Bits(Sum(ctxHist[i], seeds[j])) - entropy[i] - Bits(seeds[j]);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = j;
                }
            }

            symbol[i] = best;
            for (int s = 0; s < alphabet; s++)
            {
                seeds[best][s] += ctxHist[i][s];
            }
        }

        k = seeds.Count;
        clusterHist = seeds.ToArray();
        return symbol;
    }

    private static long[] Sum(long[] a, long[] b)
    {
        long[] r = new long[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            r[i] = a[i] + b[i];
        }

        return r;
    }

    private static double Bits(long[] h)
    {
        long total = 0;
        foreach (long v in h)
        {
            total += v;
        }

        if (total == 0)
        {
            return 0;
        }

        double bits = 0;
        double lg = Math.Log(total);
        foreach (long v in h)
        {
            if (v > 0)
            {
                bits += v * ((lg - Math.Log(v)) / Math.Log(2));
            }
        }

        return bits;
    }

    private static void EmitOpsCtx(JxlBitWriter s, List<Op> ops, int[] ctxs, PixelPlanCtx plan)
    {
        int distCluster = plan.K;
        int pos = 0;
        foreach (Op op in ops)
        {
            int litCluster = plan.ContextToCluster[ctxs[pos]];
            if (!op.Match)
            {
                (int litTok, int nbLit, int bitsLit) = PackHybridFull(LitSplit, LitMsb, LitLsb, op.A);
                plan.Codes[litCluster].WriteSymbol(s, litTok);
                s.WriteBits((uint)bitsLit, nbLit);
                pos += 1;
                continue;
            }

            (int lenBase, int nbLen, int bitsLen) = PackHybridUint(plan.SeLen, op.A - plan.MinLen);
            plan.Codes[litCluster].WriteSymbol(s, plan.Threshold + lenBase);
            s.WriteBits((uint)bitsLen, nbLen);

            (int dtok, int nbDist, int bitsDist) = PackHybridUint(plan.SeDist, op.B + NumSpecialDistances - 1);
            plan.Codes[distCluster].WriteSymbol(s, dtok);
            s.WriteBits((uint)bitsDist, nbDist);
            pos += op.A;
        }
    }

    // DecodeHistograms mirror for ANS: LZ77 disabled, an N-entry context map, ANS coding (use_prefix=0),
    // LogAlpha, per-cluster hybrid-uint configs, then each cluster's normalized distribution.
    private static void WriteAnsHistogramCtx(JxlBitWriter s, PixelPlanCtx plan, int[][] ansCounts, int logAlpha)
    {
        s.WriteBool(false); // lz77 disabled
        WriteContextMap(s, (int[])plan.ContextToCluster.Clone(), plan.K);

        s.WriteBool(false);            // use_prefix_code = false (ANS)
        s.WriteBits((uint)(logAlpha - 5), 2);
        for (int c = 0; c < plan.K; c++)
        {
            WriteUintConfig(s, LitSplit, LitMsb, LitLsb, logAlpha); // literal clusters
        }

        for (int c = 0; c < plan.K; c++)
        {
            JxlEntropy.WriteHistogram(s, ansCounts[c], JxlEntropy.HistShift);
        }
    }

    // ANS token emit (LZ77 off): every op is a single literal. Build the ANS token list for the group
    // (histogram = its literal cluster, symbol = packed residual token, plus raw mantissa bits), then let
    // the rANS writer emit the state and interleaved bits.
    private static void EmitOpsAns(JxlBitWriter s, List<Op> ops, int[] ctxs, PixelPlanCtx plan, JxlAnsWriter ans)
    {
        var tokens = new List<AnsToken>(ops.Count);
        int pos = 0;
        foreach (Op op in ops)
        {
            int litCluster = plan.ContextToCluster[ctxs[pos]];
            (int litTok, int nbLit, int bitsLit) = PackHybridFull(LitSplit, LitMsb, LitLsb, op.A);
            tokens.Add(new AnsToken(litCluster, litTok, (uint)bitsLit, nbLit));
            pos += 1;
        }

        ans.Encode(s, tokens);
    }

    // DecodeHistograms mirror: LZ77 enabled, an (N + 1)-entry context map (each pixel context to its
    // literal cluster, the distance context to the distance cluster), prefix coding, per-cluster configs.
    private static void WriteLz77HistogramCtx(JxlBitWriter s, PixelPlanCtx plan)
    {
        int numClusters = plan.K + 1;
        s.WriteBool(true); // lz77 enabled
        s.WriteU32((uint)plan.Threshold, E.Val(224), E.Val(512), E.Val(4096), E.BitsOff(15, 8)); // min_symbol
        s.WriteU32((uint)plan.MinLen, E.Val(3), E.Val(4), E.BitsOff(2, 5), E.BitsOff(8, 9)); // min_length
        WriteUintConfig(s, plan.SeLen, 0, 0, 8); // lz77 length config

        // Context map: N pixel contexts -> their literal cluster, plus the distance context -> cluster K.
        int[] map = new int[plan.N + 1];
        Array.Copy(plan.ContextToCluster, map, plan.N);
        map[plan.N] = plan.K;
        WriteContextMap(s, map, numClusters);

        s.WriteBool(true); // use_prefix_code
        for (int c = 0; c < plan.K; c++)
        {
            WriteUintConfig(s, LitSplit, LitMsb, LitLsb, 15); // literal clusters (compact hybrid-uint)
        }

        WriteUintConfig(s, plan.SeDist, 0, 0, 15); // distance cluster
        for (int c = 0; c < numClusters; c++)
        {
            s.WriteVarLenUint16(plan.Codes[c].AlphabetSize - 1);
        }

        for (int c = 0; c < numClusters; c++)
        {
            plan.Codes[c].WriteHeader(s);
        }
    }

    // Context map (context -> histogram cluster). Uses the simple bits-per-entry form when it fits
    // (<= 8 clusters), otherwise the complex form: a move-to-front transform then a prefix-coded symbol
    // stream (mirrors JxlEntropy.DecodeContextMap).
    // Largest context map still coded with raw fixed-width bits ("simple"). Above this, an MTF+ANS stream is
    // far smaller: a prefix code has a 1-bit-per-entry floor, catastrophic for the big HF map (7425 entries,
    // ~99% one cluster in long runs) — ANS codes a near-certain symbol in a small fraction of a bit.
    private const int SimpleContextMapMaxSize = 256;

    // Test hook: write a context map and return the bytes (padded to a byte), for a round-trip against
    // JxlEntropy.ReadClusters.
    internal static byte[] TestWriteContextMap(int[] map, int numClusters, out long bitLen, int padBits = 0)
    {
        var w = new JxlBitWriter();
        if (padBits > 0)
        {
            w.WriteBits(0, padBits); // misalign to mimic mid-stream writing
        }

        WriteContextMap(w, map, numClusters);
        bitLen = w.BitPosition;
        return w.ToArray();
    }

    private static void WriteContextMap(JxlBitWriter s, int[] map, int numClusters)
    {
        if (numClusters <= 1)
        {
            s.WriteBool(true);  // is_simple
            s.WriteBits(0, 2);  // bits per entry = 0 => every entry is cluster 0
            return;
        }

        if (numClusters <= 8 && map.Length <= SimpleContextMapMaxSize)
        {
            int bpe = Math.Max(1, BitLen(numClusters - 1));
            s.WriteBool(true);         // is_simple
            s.WriteBits((uint)bpe, 2); // bits per entry
            foreach (int v in map)
            {
                s.WriteBits((uint)v, bpe);
            }

            return;
        }

        // MTF + ANS. The decoder reads: is_simple=false, use_mtf, then a 1-context entropy stream
        // (DecodeHistograms(1): lz77 off, use_prefix=false, logAlpha, uint config, histogram), then `size`
        // symbols, then inverse-MTF.
        s.WriteBool(false); // not simple
        s.WriteBool(true);  // use move-to-front
        int[] mtf = MoveToFront(map);
        int maxSym = 0;
        foreach (int v in mtf)
        {
            maxSym = Math.Max(maxSym, v);
        }

        long[] freq = new long[maxSym + 1];
        foreach (int v in mtf)
        {
            freq[v]++;
        }

        // logAlpha must match the alphabet (as the modular ANS path derives it): the alias table is built for
        // this precision, so a hardcoded 8 desyncs for a small alphabet. split_exp == logAlpha keeps token ==
        // value (symbols are MTF positions < numClusters <= 2^logAlpha).
        int logAlpha = Math.Max(5, BitLen(maxSym)); // BitLen(maxSym) == CeilLog2(maxSym+1) for maxSym >= 1
        int[] norm = JxlEntropy.NormalizeCounts(freq, JxlEntropy.HistShift);
        s.WriteBool(false);                  // lz77 disabled
        s.WriteBool(false);                  // use_prefix_code = false (ANS)
        s.WriteBits((uint)(logAlpha - 5), 2);
        WriteUintConfig(s, logAlpha, 0, 0, logAlpha); // split_exp == logAlpha => token == value, no extra bits
        JxlEntropy.WriteHistogram(s, norm, JxlEntropy.HistShift);

        var ans = new JxlAnsWriter(new[] { norm }, logAlpha);
        var toks = new List<AnsToken>(mtf.Length);
        foreach (int v in mtf)
        {
            toks.Add(new AnsToken(0, v, 0, 0));
        }

        ans.Encode(s, toks);
    }

    // Forward move-to-front: emit each value's current table position, then move it to the front.
    // Inverted by JxlEntropy.InverseMtf.
    private static int[] MoveToFront(int[] values)
    {
        byte[] table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = (byte)i;
        }

        int[] outv = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            int val = values[i];
            int idx = 0;
            while (table[idx] != val)
            {
                idx++;
            }

            outv[i] = idx;
            for (int j = idx; j > 0; j--)
            {
                table[j] = table[j - 1];
            }

            table[0] = (byte)val;
        }

        return outv;
    }

    // Per-pixel residuals + contexts: walk the learned tree on the decoder's properties to pick the
    // leaf's predictor (weighted or gradient) and its context, then emit the packed residual. Uses the
    // learner's shared ComputePixel so encoder and learner never diverge. Writes stream[off..]/ctxs[off..].
    private static void ComputeResidualTokensCtx(EncChannel ch, int chan, LearnedTree tree, int[] stream, int[] ctxs, int off, ref int maxToken, WpHeader wpHeader, int[][] refs, int groupId = 0)
    {
        int w = ch.W, h = ch.H;
        int[] px = ch.Data;
        var wp = new WpState(wpHeader, w);
        var buf = new List<long>(1);
        int[] props = new int[16 + (4 * JxlTreeLearner.MaxRefChannels)];
        long[] guesses = new long[JxlTreeLearner.CandidatePredictors.Length];
        int prevGrad = 0;
        int local = maxToken;
        for (int y = 0; y < h; y++)
        {
            prevGrad = 0;
            for (int x = 0; x < w; x++)
            {
                JxlTreeLearner.ComputePixel(px, w, chan, groupId, x, y, wp, buf, props, ref prevGrad, guesses, refs);
                MaTreeNode leaf = tree.Walk(props);
                long guess = guesses[JxlTreeLearner.PredictorIndex(leaf.Predictor)];
                int pixel = px[(y * w) + x];
                int residual = pixel - (int)guess;
                int token = (int)(uint)((residual << 1) ^ (residual >> 31));
                int idx = off + (y * w) + x;
                stream[idx] = token;
                ctxs[idx] = leaf.Ctx;
                if (token > local)
                {
                    local = token;
                }

                wp.Update(pixel, x, y);
            }
        }

        maxToken = local;
    }

    // GroupHeader for the global modular stream: use_global tree+code, default weighted-predictor
    // header, and one transform — the chosen RCT, inverted after decode.
    private static void WriteGlobalGroupHeader(JxlBitWriter s, int rctType, int wpMode)
    {
        s.WriteBool(true);  // use_global tree + code
        WriteWpHeaderBits(s, wpMode);
        WriteRctTransform(s, rctType);
    }

    // Self-correcting weighted-predictor (predictor 6) residuals, packed with the JXL signed->unsigned
    // mapping. The encoder runs the decoder's exact WpState so the predictions — and thus residuals —
    // match the decode bit-for-bit.
    // DecodeHistograms mirror for a single-histogram, prefix-coded, LZ77-free entropy code.
    private static void WriteHistogramsHeader(JxlBitWriter w, int numContexts, JxlPrefixCode code)
    {
        w.WriteBool(false); // lz77 disabled
        if (numContexts > 1)
        {
            // Simple context map, bits-per-entry 0 => every context maps to cluster 0 (one histogram).
            w.WriteBool(true);
            w.WriteBits(0, 2);
        }

        w.WriteBool(true); // use_prefix_code
        WriteUintConfig(w, splitExp: 15, msb: 0, lsb: 0, logAlpha: 15);
        w.WriteVarLenUint16(code.AlphabetSize - 1);
        code.WriteHeader(w);
    }

    private static void WriteUintConfig(JxlBitWriter w, int splitExp, int msb, int lsb, int logAlpha)
    {
        w.WriteBits((uint)splitExp, JxlBits.CeilLog2(logAlpha + 1));
        if (splitExp != logAlpha)
        {
            w.WriteBits((uint)msb, JxlBits.CeilLog2(splitExp + 1));
            w.WriteBits((uint)lsb, JxlBits.CeilLog2(splitExp - msb + 1));
        }
    }
}
