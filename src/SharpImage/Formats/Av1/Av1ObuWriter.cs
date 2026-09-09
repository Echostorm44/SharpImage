// AV1 OBU codestream writer for the AVIF (still-image) encoder. Emits the uncompressed headers — OBU framing,
// sequence header, frame header — using Av1BitWriter (the inverse of the decoder's Av1GetBits). The compressed
// tile symbol payload comes from Av1MsacWriter. Verified by parsing our own output with Av1ObuParser.
//
// Scope (step 2 of the AV1 encoder): a minimal still picture — reduced_still_picture_header, one frame,
// key-frame intra, all features off (no CDEF/restoration/superres/loop filter/segmentation/delta-q).
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Av1;

internal static class Av1ObuWriter
{
    /// <summary>Minimal still-image sequence configuration.</summary>
    internal readonly struct SeqConfig
    {
        public readonly int Width;
        public readonly int Height;
        public readonly bool Monochrome;

        public SeqConfig(int width, int height, bool monochrome)
        {
            Width = width;
            Height = height;
            Monochrome = monochrome;
        }
    }

    /// <summary>Wraps a payload in an OBU: header byte + leb128 size + payload. No extension header.</summary>
    internal static byte[] WrapObu(Av1ObuType type, ReadOnlySpan<byte> payload)
    {
        // forbidden(0) | type(4) | extension(0) | has_size(1) | reserved(0)
        byte hdr = (byte)(((int)type << 3) | 0b10);
        var sizeWriter = new Av1BitWriter();
        sizeWriter.PutUleb128((uint)payload.Length);
        byte[] sizeBytes = sizeWriter.ToArray();

        var outBytes = new byte[1 + sizeBytes.Length + payload.Length];
        outBytes[0] = hdr;
        sizeBytes.CopyTo(outBytes, 1);
        payload.CopyTo(outBytes.AsSpan(1 + sizeBytes.Length));
        return outBytes;
    }

    /// <summary>Number of bits needed to hold (value) as an unsigned max field (frame_width_bits etc.).</summary>
    private static int BitsFor(int value)
    {
        int n = 0;
        while ((1 << n) <= value)
        {
            n++;
        }

        return n < 1 ? 1 : n;
    }

    /// <summary>Writes the sequence header OBU payload (no OBU framing). Mirrors Av1ObuParser.ParseSequenceHeader
    /// for the reduced_still_picture_header path.</summary>
    internal static byte[] WriteSequenceHeaderPayload(in SeqConfig cfg)
    {
        var w = new Av1BitWriter();

        w.PutBits(0, 3);          // seq_profile = 0
        w.PutBool(true);          // still_picture = 1
        w.PutBool(true);          // reduced_still_picture_header = 1

        // reduced still: seq_level_idx[0] = f(5) (parser splits 3 major + 2 minor). 0 = level 2.0.
        w.PutBits(0, 3);          // MajorLevel bits
        w.PutBits(0, 2);          // MinorLevel bits

        int widthBits = BitsFor(cfg.Width - 1);
        int heightBits = BitsFor(cfg.Height - 1);
        w.PutBits((uint)(widthBits - 1), 4);   // frame_width_bits_minus_1
        w.PutBits((uint)(heightBits - 1), 4);  // frame_height_bits_minus_1
        w.PutBits((uint)(cfg.Width - 1), widthBits);   // max_frame_width_minus_1
        w.PutBits((uint)(cfg.Height - 1), heightBits); // max_frame_height_minus_1

        // reduced still → frame_id_numbers_present skipped.
        w.PutBool(false);         // use_128x128_superblock = 0 (64x64 SBs)
        w.PutBool(false);         // enable_filter_intra = 0
        w.PutBool(false);         // enable_intra_edge_filter = 0

        // reduced still → inter tools block skipped; screen_content_tools/force_integer_mv default Adaptive.
        w.PutBool(false);         // enable_superres = 0
        w.PutBool(false);         // enable_cdef = 0
        w.PutBool(false);         // enable_restoration = 0

        // color_config
        w.PutBit(0);              // high_bitdepth = 0 (8-bit, profile 0)
        w.PutBool(cfg.Monochrome); // mono_chrome (profile != 1)
        w.PutBool(false);         // color_description_present_flag = 0

        if (cfg.Monochrome)
        {
            w.PutBit(0);          // color_range = 0 (studio)
            // subsampling/chroma-sample-position implied (I400)
        }
        else
        {
            // Non-monochrome minimal path not used in step 2.
            throw new NotSupportedException("Only monochrome sequence header is implemented in step 2.");
        }

        if (!cfg.Monochrome)
        {
            w.PutBool(false);     // separate_uv_delta_q
        }

        w.PutBool(false);         // film_grain_params_present = 0

        return w.ToArray();
    }

    /// <summary>Writes the frame header for a reduced-still monochrome key frame with all features off. Mirrors
    /// Av1ObuParser.ParseFrameHeader. <paramref name="baseQIdx"/> must be non-zero (0 would flag lossless, which
    /// takes a different coding path). When <paramref name="isObuFrame"/> is true the header is only byte-aligned
    /// (tile data follows in the same OBU_FRAME); otherwise a trailing one-bit terminates a standalone
    /// OBU_FRAME_HEADER.</summary>
    internal static byte[] WriteFrameHeaderPayload(int baseQIdx, bool isObuFrame)
    {
        if (baseQIdx <= 0 || baseQIdx > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(baseQIdx), "base_q_idx must be in [1,255] (0 = lossless path).");
        }

        var w = new Av1BitWriter();

        // reduced_still_picture_header ⇒ show_existing_frame / frame_type / show_frame / error_resilient are all
        // implied; nothing is written until here.
        w.PutBool(true);          // disable_cdf_update = 1 (static default CDFs — no adaptation to mirror)
        w.PutBool(false);         // allow_screen_content_tools = 0 (ScreenContentTools is Adaptive)
        // force_integer_mv implied (intra); frame_id absent; frame_size_override absent (reduced still).
        // primary_ref_frame implied NONE (intra); decoder model absent.
        // refresh_frame_flags = 0xFF implied (key + show_frame).

        // read_frame_size (useRef=false): frame_size_override=0 ⇒ dims from seq; superres off ⇒ no bit.
        w.PutBool(false);         // have_render_size = 0 (render size = frame size)
        // allow_intra_bc skipped (screen content tools off).
        // refresh_context skipped (reduced still).

        // tile_info: one 64x64 superblock ⇒ sbw=sbh=1 ⇒ only uniform_tile flag, no further bits.
        w.PutBool(true);          // uniform_tile_spacing_flag

        // quantization_params
        w.PutBits((uint)baseQIdx, 8); // base_q_idx
        w.PutBool(false);         // diff_uv_delta not reached (mono); y_dc: delta_coded = 0
        w.PutBool(false);         // using_qmatrix = 0

        // segmentation_params
        w.PutBool(false);         // segmentation_enabled = 0

        // delta_q_params (base_q_idx != 0)
        w.PutBool(false);         // delta_q_present = 0

        // loop_filter_params (not lossless, not intrabc)
        w.PutBits(0, 6);          // loop_filter_level[0] = 0
        w.PutBits(0, 6);          // loop_filter_level[1] = 0
        // mono ⇒ no U/V levels
        w.PutBits(0, 3);          // loop_filter_sharpness = 0
        w.PutBool(false);         // loop_filter_delta_enabled = 0

        // cdef_params skipped (enable_cdef=0); lr_params skipped (enable_restoration=0)

        // read_tx_mode (not lossless)
        w.PutBool(false);         // tx_mode_select = 0 ⇒ TX_MODE_LARGEST

        // frame_reference_mode / skip_mode / warp skipped (intra)
        w.PutBool(true);          // reduced_tx_set = 1

        // global_motion skipped (intra); film_grain skipped (not present)

        if (!isObuFrame)
        {
            w.TrailingBits();     // trailing_one_bit + byte alignment (OBU_FRAME_HEADER)
        }

        return w.ToArray();       // ToArray byte-aligns (OBU_FRAME: tile data follows the aligned header)
    }
}
