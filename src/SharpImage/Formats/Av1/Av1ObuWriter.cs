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
    /// <summary>CICP colour description + range written to the sequence header's color_config (and mirrored by the
    /// AVIF 'colr' nclx box). <see cref="Legacy"/> (all 2 = unspecified, full range) writes no description.</summary>
    internal readonly record struct Av1ColorDesc(int Primaries, int Transfer, int Matrix, bool FullRange)
    {
        internal static Av1ColorDesc Legacy => new(2, 2, 2, true);
        internal bool HasDescription => Primaries != 2 || Transfer != 2 || Matrix != 2;
        /// <summary>BT.709 primaries + sRGB transfer + identity matrix: AV1's implied 4:4:4 full-range sRGB case
        /// (color_range and subsampling are not coded).</summary>
        internal bool IsSrgbIdentity => Primaries == 1 && Transfer == 13 && Matrix == 0;
    }

    internal readonly struct SeqConfig
    {
        public readonly int Width;
        public readonly int Height;
        public readonly bool Monochrome;
        public readonly bool EnableFilterIntra;
        public readonly int BitDepth;   // 8, 10 or 12
        public readonly Av1PixelLayout Layout; // chroma layout of a colour stream (ignored when Monochrome)
        public readonly Av1ColorDesc Color;
        public readonly Av1FilmGrainData? FilmGrain;   // the ambient film grain when this stream was configured
        public readonly LayeredStream? Layered;        // the ambient layered (progressive) stream, if any

        public SeqConfig(int width, int height, bool monochrome, bool enableFilterIntra = false, int bitDepth = 8,
            Av1PixelLayout layout = Av1PixelLayout.I420, Av1ColorDesc? color = null)
        {
            Color = color ?? Av1ColorDesc.Legacy;
            FilmGrain = ActiveFilmGrain;
            Layered = t_layered;
            Width = width;
            Height = height;
            Monochrome = monochrome;
            EnableFilterIntra = enableFilterIntra;
            BitDepth = bitDepth;
            Layout = monochrome ? Av1PixelLayout.I400 : layout;
        }
    }

    // Film grain for the stream being built: set by the AVIF encoder around the colour stream only (alpha builds run
    // under SuppressFilmGrain), read by SeqConfig (film_grain_params_present) and the frame header writer.
    [ThreadStatic] private static Av1FilmGrainData? t_filmGrain;
    [ThreadStatic] private static bool t_filmGrain420;
    [ThreadStatic] private static int t_filmGrainSuppressed;

    internal static Av1FilmGrainData? ActiveFilmGrain => t_filmGrainSuppressed > 0 ? null : t_filmGrain;

    /// <summary>Sets the ambient film grain for the streams built inside the scope (null = none).</summary>
    internal static FilmGrainScope UseFilmGrain(Av1FilmGrainData? grain, bool is420) => new(grain, is420);

    internal readonly struct FilmGrainScope : IDisposable
    {
        private readonly Av1FilmGrainData? prev;
        private readonly bool prev420;
        public FilmGrainScope(Av1FilmGrainData? grain, bool is420)
        {
            prev = t_filmGrain; prev420 = t_filmGrain420;
            t_filmGrain = grain; t_filmGrain420 = is420;
        }
        public void Dispose() { t_filmGrain = prev; t_filmGrain420 = prev420; }
    }

    internal readonly struct SuppressFilmGrain : IDisposable
    {
        private readonly bool active;
        public SuppressFilmGrain(bool suppress) { active = suppress; if (suppress) t_filmGrainSuppressed++; }
        public void Dispose() { if (active) t_filmGrainSuppressed--; }
    }

    /// <summary>film_grain_params() for a shown key frame (spec 5.9.30; libaom write_film_grain_params):
    /// apply_grain, grain_seed, then the full parameter set (update_grain is implied for non-inter frames).</summary>
    private static unsafe void WriteFilmGrainParams(Av1BitWriter w, bool monochrome, bool interFrame = false)
    {
        if (ActiveFilmGrain is not Av1FilmGrainData fg) return;
        w.PutBool(true);                            // apply_grain
        w.PutBits(fg.Seed & 0xFFFF, 16);            // grain_seed
        if (interFrame) w.PutBool(true);            // update_grain (INTER_FRAME): parameters follow
        w.PutBits((uint)fg.NumYPoints, 4);
        for (int i = 0; i < fg.NumYPoints; i++)
        {
            w.PutBits(fg.YPoints[i * 2], 8);
            w.PutBits(fg.YPoints[i * 2 + 1], 8);
        }
        if (!monochrome) w.PutBool(fg.ChromaScalingFromLuma != 0);
        // Chroma point counts are absent for mono / chroma-from-luma / 4:2:0 without luma points (AvifFilmGrain.ToAv1
        // has zeroed the chroma points in those cases).
        if (!monochrome && fg.ChromaScalingFromLuma == 0 && !(fg.NumYPoints == 0 && t_filmGrain420))
        {
            w.PutBits((uint)fg.NumUvPoints0, 4);
            for (int i = 0; i < fg.NumUvPoints0; i++) { w.PutBits(fg.UvPoints[i * 2], 8); w.PutBits(fg.UvPoints[i * 2 + 1], 8); }
            w.PutBits((uint)fg.NumUvPoints1, 4);
            for (int i = 0; i < fg.NumUvPoints1; i++) { w.PutBits(fg.UvPoints[20 + i * 2], 8); w.PutBits(fg.UvPoints[21 + i * 2], 8); }
        }
        w.PutBits((uint)(fg.ScalingShift - 8), 2);  // grain_scaling_minus_8
        w.PutBits((uint)fg.ArCoeffLag, 2);
        int numPosLuma = 2 * fg.ArCoeffLag * (fg.ArCoeffLag + 1);
        int numPosChroma = numPosLuma + (fg.NumYPoints > 0 ? 1 : 0);
        if (fg.NumYPoints > 0)
            for (int i = 0; i < numPosLuma; i++) w.PutBits((uint)(fg.ArCoeffsY[i] + 128), 8);
        if (fg.NumUvPoints0 > 0 || fg.ChromaScalingFromLuma != 0)
            for (int i = 0; i < numPosChroma; i++) w.PutBits((uint)(fg.ArCoeffsUv[i] + 128), 8);
        if (fg.NumUvPoints1 > 0 || fg.ChromaScalingFromLuma != 0)
            for (int i = 0; i < numPosChroma; i++) w.PutBits((uint)(fg.ArCoeffsUv[28 + i] + 128), 8);
        w.PutBits((uint)(fg.ArCoeffShift - 6), 2);  // ar_coeff_shift_minus_6
        w.PutBits((uint)fg.GrainScaleShift, 2);
        if (fg.NumUvPoints0 > 0)
        {
            w.PutBits((uint)(fg.UvMult0 + 128), 8);
            w.PutBits((uint)(fg.UvLumaMult0 + 128), 8);
            w.PutBits((uint)(fg.UvOffset0 + 256), 9);
        }
        if (fg.NumUvPoints1 > 0)
        {
            w.PutBits((uint)(fg.UvMult1 + 128), 8);
            w.PutBits((uint)(fg.UvLumaMult1 + 128), 8);
            w.PutBits((uint)(fg.UvOffset1 + 256), 9);
        }
        w.PutBool(fg.OverlapFlag != 0);
        w.PutBool(fg.ClipToRestrictedRange != 0);
    }

    /// <summary>
    /// A spatially layered still image (progressive AVIF, as avifenc --progressive / --layered): one key frame (layer 0)
    /// then an intra-only frame per further layer, all shown in one temporal unit with OBU extension spatial ids and one
    /// operating point per decodable prefix of layers. While the scope is active the writers emit the non-reduced
    /// sequence header, explicit per-layer frame sizes and extension headers for <see cref="Current"/>.
    /// </summary>
    internal sealed class LayeredStream
    {
        public int Layers, MaxWidth, MaxHeight, Current;
        public int[] Widths = [], Heights = [];

        /// <summary>
        /// A plain image sequence (animated AVIF track) rather than spatial layers: one operating point (idc 0), no
        /// OBU extension headers, every frame a shown key frame at the sequence's maximum size.
        /// </summary>
        public bool Sequence;

        /// <summary>
        /// Sequence only: the frame being written is an INTER_FRAME predicted from reference slot 0 (LAST), with every
        /// ref_frame_idx pointing there, no CDF inheritance (primary_ref_frame NONE), quarter-pel MVs, the regular
        /// 8-tap filter, simple motion only, single references and no global motion. <see cref="RefreshFlags"/> names
        /// the slots it replaces.
        /// </summary>
        public bool InterFrame;
        public int RefreshFlags = 1;

        /// <summary>Inter frames: the slot each of LAST..ALTREF reads (ref_frame_idx).</summary>
        public int[] RefFrameIdx = new int[7];

        /// <summary>Inter frames: false codes a hidden frame (showable later through show_existing_frame).</summary>
        public bool ShowFrame = true;

        /// <summary>Inter frames: reference_select — blocks may use compound (two-reference) prediction.</summary>
        public bool ReferenceSelect;
    }

    /// <summary>A temporal unit's OBU_FRAME_HEADER with show_existing_frame = 1: displays reference slot
    /// <paramref name="slot"/> (a hidden, showable inter frame) without coding anything.</summary>
    internal static byte[] ShowExistingFrameObu(int slot)
    {
        var w = new Av1BitWriter();
        w.PutBool(true);                  // show_existing_frame
        w.PutBits((uint)slot, 3);         // frame_to_show_map_idx
        w.TrailingBits();
        return WrapObu(Av1ObuType.FrameHeader, w.ToArray());
    }

    [ThreadStatic] private static LayeredStream? t_layered;
    internal static bool CurrentReferenceSelect => t_layered is { Sequence: true, InterFrame: true, ReferenceSelect: true };

    internal readonly struct LayeredScope : IDisposable
    {
        private readonly LayeredStream? prev;
        public LayeredScope(LayeredStream? s) { prev = t_layered; t_layered = s; }
        public void Dispose() => t_layered = prev;
    }

    internal static LayeredScope UseLayers(LayeredStream? s) => new(s);

    // Non-reduced sequence header for a layered stream (spec 5.5): no timing info, one operating point per prefix of
    // spatial layers (op 0 = all), explicit max frame size, order hints off (intra frames only), screen-content tools
    // and integer MV "select" (as the reduced header implies, so frame headers code the same fields).
    private static void WriteLayeredSequenceStart(Av1BitWriter w, in SeqConfig cfg, LayeredStream ls, int seqProfile)
    {
        w.PutBits((uint)seqProfile, 3);
        w.PutBool(false);         // still_picture = 0 (several frames)
        w.PutBool(false);         // reduced_still_picture_header = 0
        w.PutBool(false);         // timing_info_present_flag
        w.PutBool(false);         // initial_display_delay_present_flag
        w.PutBits((uint)(ls.Sequence ? 0 : ls.Layers - 1), 5);   // operating_points_cnt_minus_1
        if (ls.Sequence)
        {
            w.PutBits(0, 12);                 // operating_point_idc: no scalability
            int seqLevel = SeqLevelIdx(ls.MaxWidth, ls.MaxHeight);
            w.PutBits((uint)seqLevel, 5);     // seq_level_idx
            if (seqLevel > 7) w.PutBool(false);
        }
        else for (int op = 0; op < ls.Layers; op++)
        {
            int top = ls.Layers - 1 - op;     // highest spatial layer of this operating point
            uint idc = (uint)((((1 << (top + 1)) - 1) << 8) | 1);
            w.PutBits(idc, 12);               // operating_point_idc
            int level = SeqLevelIdx(ls.Widths[top], ls.Heights[top]);
            w.PutBits((uint)level, 5);        // seq_level_idx
            if (level > 7) w.PutBool(false);  // seq_tier
        }
        int widthBits = BitsFor(ls.MaxWidth - 1), heightBits = BitsFor(ls.MaxHeight - 1);
        w.PutBits((uint)(widthBits - 1), 4);
        w.PutBits((uint)(heightBits - 1), 4);
        w.PutBits((uint)(ls.MaxWidth - 1), widthBits);
        w.PutBits((uint)(ls.MaxHeight - 1), heightBits);
        w.PutBool(false);         // frame_id_numbers_present_flag
        w.PutBool(false);         // use_128x128_superblock
        w.PutBool(cfg.EnableFilterIntra);
        w.PutBool(Av1StillImageEncoder.UseIntraEdgeFilter);
        w.PutBool(false);         // enable_interintra_compound
        w.PutBool(false);         // enable_masked_compound
        w.PutBool(false);         // enable_warped_motion
        w.PutBool(false);         // enable_dual_filter
        w.PutBool(false);         // enable_order_hint
        w.PutBool(true);          // seq_choose_screen_content_tools (SELECT)
        w.PutBool(true);          // seq_choose_integer_mv (SELECT)
    }

    // uncompressed_header() fields a reduced still picture implies, for layer ls.Current: a key frame for the base
    // layer, intra-only frames above it; every frame shown and explicitly sized (render size = the full image).
    private static void WriteLayeredFramePrefix(Av1BitWriter w, LayeredStream ls, bool screenContentTools)
    {
        if (ls.Sequence && ls.InterFrame)
        {
            w.PutBool(false);                  // show_existing_frame
            w.PutBits(1, 2);                   // frame_type: INTER_FRAME
            w.PutBool(ls.ShowFrame);           // show_frame
            if (!ls.ShowFrame) w.PutBool(true);   // showable_frame (shown later by show_existing_frame)
            w.PutBool(false);                  // error_resilient_mode
            w.PutBool(false);                  // disable_cdf_update
            w.PutBool(screenContentTools);     // allow_screen_content_tools (SELECT)
            if (screenContentTools) w.PutBool(false);   // force_integer_mv (SELECT)
            w.PutBool(false);                  // frame_size_override_flag
            // order_hint: 0 bits (enable_order_hint = 0)
            w.PutBits(7, 3);                   // primary_ref_frame = PRIMARY_REF_NONE: default CDFs, no inherited state
            w.PutBits((uint)ls.RefreshFlags, 8);   // refresh_frame_flags
            // frame_refs_short_signaling absent (no order hints).
            for (int i = 0; i < 7; i++) w.PutBits((uint)ls.RefFrameIdx[i], 3);   // ref_frame_idx[i]
            // frame_size(): the sequence maximum (no override), no superres; render_size():
            w.PutBool(false);                  // render_and_frame_size_different
            w.PutBool(false);                  // allow_high_precision_mv (quarter-pel)
            w.PutBool(false);                  // is_filter_switchable
            w.PutBits(0, 2);                   // interpolation_filter = EIGHTTAP (regular)
            w.PutBool(false);                  // is_motion_mode_switchable
            // use_ref_frame_mvs absent (enable_ref_frame_mvs = 0)
            w.PutBool(true);                   // disable_frame_end_update_cdf
            return;
        }
        bool key = ls.Sequence || ls.Current == 0;
        w.PutBool(false);                      // show_existing_frame
        w.PutBits(key ? 0u : 2u, 2);           // frame_type: KEY_FRAME / INTRA_ONLY_FRAME
        w.PutBool(true);                       // show_frame
        if (!key) w.PutBool(false);            // error_resilient_mode (implied 1 for a shown key frame)
        w.PutBool(false);                      // disable_cdf_update
        w.PutBool(screenContentTools);         // allow_screen_content_tools (SELECT)
        if (screenContentTools) w.PutBool(false);   // force_integer_mv (SELECT; intra overrides it to 1)
        if (ls.Sequence)
        {
            // Full-size key frame: no size override (the sequence maximum), render size = frame size.
            w.PutBool(false);                  // frame_size_override_flag
            w.PutBool(false);                  // render_and_frame_size_different
            if (screenContentTools) w.PutBool(false);   // allow_intrabc
            w.PutBool(true);                   // disable_frame_end_update_cdf
            return;
        }
        w.PutBool(true);                       // frame_size_override_flag
        // order_hint: 0 bits; primary_ref_frame: none (intra).
        if (!key) w.PutBits(1u << ls.Current, 8);   // refresh_frame_flags (a key frame refreshes all)
        int fw = ls.Widths[ls.Current], fh = ls.Heights[ls.Current];
        w.PutBits((uint)(fw - 1), BitsFor(ls.MaxWidth - 1));    // frame_width_minus_1
        w.PutBits((uint)(fh - 1), BitsFor(ls.MaxHeight - 1));   // frame_height_minus_1
        bool renderDiff = fw != ls.MaxWidth || fh != ls.MaxHeight;
        w.PutBool(renderDiff);                 // render_and_frame_size_different
        if (renderDiff)
        {
            w.PutBits((uint)(ls.MaxWidth - 1), 16);
            w.PutBits((uint)(ls.MaxHeight - 1), 16);
        }
        if (screenContentTools) w.PutBool(false);   // allow_intrabc
        w.PutBool(true);                       // disable_frame_end_update_cdf (coded when not reduced)
    }

    /// <summary>Wraps a payload in an OBU with an extension header (temporal_id, spatial_id).</summary>
    internal static byte[] WrapObuExtension(Av1ObuType type, ReadOnlySpan<byte> payload, int temporalId, int spatialId)
    {
        var sizeWriter = new Av1BitWriter();
        sizeWriter.PutUleb128((uint)payload.Length);
        byte[] sizeBytes = sizeWriter.ToArray();
        var outBytes = new byte[2 + sizeBytes.Length + payload.Length];
        outBytes[0] = (byte)(((int)type << 3) | 0b110);                   // extension + has_size
        outBytes[1] = (byte)((temporalId << 5) | ((spatialId & 3) << 3));  // temporal_id(3) spatial_id(2) reserved(3)
        sizeBytes.CopyTo(outBytes, 2);
        payload.CopyTo(outBytes.AsSpan(2 + sizeBytes.Length));
        return outBytes;
    }

    /// <summary>Wraps a payload in an OBU: header byte + leb128 size + payload. No extension header.</summary>
    internal static byte[] WrapObu(Av1ObuType type, ReadOnlySpan<byte> payload)
    {
        if (t_layered is { Sequence: false } ls && type is Av1ObuType.Frame or Av1ObuType.FrameHeader or Av1ObuType.TileGroup)
            return WrapObuExtension(type, payload, 0, ls.Current);
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

    // AV1 Annex A picture-size limits (MaxPicSize, MaxHSize, MaxVSize) per seq_level_idx; the lowest level that fits
    // is signalled, as libaom does. Beyond level 6.x: 31 (maximum parameters).
    private static readonly (int Idx, long MaxPic, int MaxH, int MaxV)[] Levels =
    [
        (0, 147456, 2048, 1152), (1, 278784, 2816, 1584), (4, 665856, 4352, 2448), (5, 1065024, 5504, 3096),
        (8, 2359296, 6144, 3456), (12, 8912896, 8192, 4352), (16, 35651584, 16384, 8704),
    ];

    internal static int SeqLevelIdx(int width, int height)
    {
        foreach (var l in Levels)
            if ((long)width * height <= l.MaxPic && width <= l.MaxH && height <= l.MaxV) return l.Idx;
        return 31;
    }

    [ThreadStatic] private static (int Cols, int Rows) t_tileLog2Request;

    // All of the writer's ambient (thread-static) state, so an encoder's worker threads build headers exactly as the
    // calling thread would (film grain, layered / sequence stream, tiling request).
    internal readonly record struct ThreadState(Av1FilmGrainData? FilmGrain, bool FilmGrain420, int FilmGrainSuppressed,
        LayeredStream? Layered, (int Cols, int Rows) TileLog2);
    internal static ThreadState CaptureThreadState() => new(t_filmGrain, t_filmGrain420, t_filmGrainSuppressed, t_layered, t_tileLog2Request);
    internal static ThreadState ExchangeThreadState(ThreadState s)
    {
        var prev = CaptureThreadState();
        t_filmGrain = s.FilmGrain; t_filmGrain420 = s.FilmGrain420; t_filmGrainSuppressed = s.FilmGrainSuppressed;
        t_layered = s.Layered; t_tileLog2Request = s.TileLog2;
        return prev;
    }

    internal readonly struct TilingScope : IDisposable
    {
        private readonly (int, int) prev;
        public TilingScope((int Cols, int Rows) log2) { prev = t_tileLog2Request; t_tileLog2Request = log2; }
        public void Dispose() => t_tileLog2Request = prev;
    }

    /// <summary>Requests log2 tile columns / rows (aomenc --tile-columns / --tile-rows, avifenc --tilecolslog2 /
    /// --tilerowslog2) for everything encoded in the scope; each is clamped to what tile_info() allows, as libaom's
    /// av1_set_tile_info does.</summary>
    internal static TilingScope UseTiling((int Cols, int Rows) log2) => new(log2);

    /// <summary>The uniform tile layout for a frame of <paramref name="sbCols"/> x <paramref name="sbRows"/> 64x64
    /// superblocks: the requested log2 tile counts (default 0) clamped as libaom does — columns to [minLog2TileCols,
    /// maxLog2TileCols], rows to [max(minLog2Tiles - cols, 0), maxLog2TileRows] — where the minima keep tiles at most 4096
    /// px wide (MAX_TILE_WIDTH) and 4096*2304 px in area (MAX_TILE_AREA). Start arrays carry a final entry = sbCols /
    /// sbRows.</summary>
    internal static (int ColsLog2, int RowsLog2, int[] ColStartSb, int[] RowStartSb) TileLayout(int sbCols, int sbRows)
    {
        const int maxTileWidthSb = 4096 >> 6, maxTileAreaSb = (4096 * 2304) >> 12;
        int minLog2Cols = TileLog2(maxTileWidthSb, sbCols);
        int minLog2Tiles = Math.Max(minLog2Cols, TileLog2(maxTileAreaSb, sbRows * sbCols));
        int colsLog2 = Math.Min(Math.Max(t_tileLog2Request.Cols, minLog2Cols), TileMaxLog2(sbCols));
        int rowsLog2 = Math.Min(Math.Max(t_tileLog2Request.Rows, Math.Max(minLog2Tiles - colsLog2, 0)), TileMaxLog2(sbRows));
        static int[] Starts(int sb, int log2)
        {
            int size = (sb + (1 << log2) - 1) >> log2;
            var s = new List<int>();
            for (int i = 0; i < sb; i += size) s.Add(i);
            s.Add(sb);
            return s.ToArray();
        }
        return (colsLog2, rowsLog2, Starts(sbCols, colsLog2), Starts(sbRows, rowsLog2));
    }

    private static int TileLog2(int blkSize, int target)
    {
        int k = 0;
        while ((blkSize << k) < target) k++;
        return k;
    }

    // TileMaxLog2 for one axis: smallest k with (1<<k) >= min(sb, MaxTiles=64) (mirrors TileLog2(1, min(sb,64))).
    private static int TileMaxLog2(int sb)
    {
        int tgt = Math.Min(sb, 64);
        int k = 0;
        while ((1 << k) < tgt)
        {
            k++;
        }

        return k;
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

    /// <summary>AV1 seq_profile (spec Annex A): Main (0) = 8/10-bit 4:2:0 + monochrome; High (1) = 8/10-bit 4:4:4;
    /// Professional (2) = 4:2:2 at any depth and every 12-bit format.</summary>
    internal static int SeqProfile(in SeqConfig cfg) => SeqProfile(cfg.BitDepth, cfg.Layout);

    internal static int SeqProfile(int bitDepth, Av1PixelLayout layout) =>
        bitDepth == 12 || layout == Av1PixelLayout.I422 ? 2 : layout == Av1PixelLayout.I444 ? 1 : 0;

    /// <summary>Writes the sequence header OBU payload (no OBU framing). Mirrors Av1ObuParser.ParseSequenceHeader
    /// for the reduced_still_picture_header path.</summary>
    internal static byte[] WriteSequenceHeaderPayload(in SeqConfig cfg)
    {
        var w = new Av1BitWriter();

        // seq_profile: 0 (Main) covers 8/10-bit 4:2:0 + mono; 12-bit needs 2 (Professional).
        int seqProfile = SeqProfile(cfg);
        if (cfg.Layered is { } layered)
        {
            WriteLayeredSequenceStart(w, cfg, layered, seqProfile);
            goto afterTools;
        }
        w.PutBits((uint)seqProfile, 3);
        w.PutBool(true);          // still_picture = 1
        w.PutBool(true);          // reduced_still_picture_header = 1

        // reduced still: seq_level_idx[0] = f(5) (parser splits 3 major + 2 minor): the lowest level the size fits.
        int level = SeqLevelIdx(cfg.Width, cfg.Height);
        w.PutBits((uint)(level >> 2), 3);   // MajorLevel bits
        w.PutBits((uint)(level & 3), 2);    // MinorLevel bits

        int widthBits = BitsFor(cfg.Width - 1);
        int heightBits = BitsFor(cfg.Height - 1);
        w.PutBits((uint)(widthBits - 1), 4);   // frame_width_bits_minus_1
        w.PutBits((uint)(heightBits - 1), 4);  // frame_height_bits_minus_1
        w.PutBits((uint)(cfg.Width - 1), widthBits);   // max_frame_width_minus_1
        w.PutBits((uint)(cfg.Height - 1), heightBits); // max_frame_height_minus_1

        // reduced still → frame_id_numbers_present skipped.
        w.PutBool(false);         // use_128x128_superblock = 0 (64x64 SBs)
        w.PutBool(cfg.EnableFilterIntra);  // enable_filter_intra (set only on the multi-SB colour path that emits it)
        w.PutBool(Av1StillImageEncoder.UseIntraEdgeFilter);  // enable_intra_edge_filter

        // reduced still → inter tools block skipped; screen_content_tools/force_integer_mv default Adaptive.
    afterTools:
        w.PutBool(false);         // enable_superres = 0
        w.PutBool(true);          // enable_cdef = 1 (frame header carries cdef_params; strengths may be 0 = no-op)
        w.PutBool(false);         // enable_restoration = 0

        // color_config (AV1 spec 5.5.2)
        w.PutBool(cfg.BitDepth > 8);                      // high_bitdepth
        if (seqProfile == 2 && cfg.BitDepth > 8)
            w.PutBool(cfg.BitDepth == 12);                // twelve_bit
        if (seqProfile != 1)
            w.PutBool(cfg.Monochrome); // mono_chrome (profile 1 is always colour 4:4:4)
        w.PutBool(cfg.Color.HasDescription);   // color_description_present_flag
        if (cfg.Color.HasDescription)
        {
            w.PutBits((uint)cfg.Color.Primaries, 8);
            w.PutBits((uint)cfg.Color.Transfer, 8);
            w.PutBits((uint)cfg.Color.Matrix, 8);
        }

        if (cfg.Monochrome)
        {
            // color_range (legacy/default 1 = full). Monochrome streams are also AVIF alpha planes, and libavif/
            // Chrome honour this flag for alpha: writing 0 (studio) made them range-expand our full-range alpha.
            w.PutBit(cfg.Color.FullRange ? 1u : 0u);
            // subsampling/chroma-sample-position implied (I400)
        }
        else if (cfg.Color.IsSrgbIdentity)
        {
            // BT.709/sRGB/Identity: color_range = 1 and 4:4:4 are implied — nothing coded but separate_uv_delta_q.
            if (cfg.Layout != Av1PixelLayout.I444) throw new ArgumentException("The identity matrix requires 4:4:4.");
            w.PutBool(false);     // separate_uv_delta_q = 0
        }
        else
        {
            // Colours Unspecified ⇒ the parser's "else" branch: color_range, then the subsampling (implied 1,1 by
            // profile 0 and 0,0 by profile 1; profile 2 codes subsampling_x/_y only at 12-bit, else implies 4:2:2),
            // then chroma_sample_position when subX && subY, then separate_uv_delta_q.
            if (cfg.Color.Matrix == 0 && cfg.Layout != Av1PixelLayout.I444)
                throw new ArgumentException("The identity matrix requires 4:4:4.");
            w.PutBit(cfg.Color.FullRange ? 1u : 0u);   // color_range
            int ssX = cfg.Layout == Av1PixelLayout.I444 ? 0 : 1;
            int ssY = cfg.Layout == Av1PixelLayout.I420 ? 1 : 0;
            if (seqProfile == 2 && cfg.BitDepth == 12)
            {
                w.PutBit((uint)ssX);                 // subsampling_x
                if (ssX != 0) w.PutBit((uint)ssY);   // subsampling_y
            }
            if (ssX != 0 && ssY != 0)
                w.PutBits(0, 2);  // chroma_sample_position = Unknown
            w.PutBool(false);     // separate_uv_delta_q = 0
        }

        w.PutBool(cfg.FilmGrain != null);   // film_grain_params_present

        // open_bitstream_unit() appends trailing_bits() to every OBU except TILE_GROUP/TILE_LIST/FRAME — a 1 bit
        // then zero padding. Our own parser ignores it, but conformant parsers (ffmpeg/dav1d CBS) enforce it.
        w.TrailingBits();

        return w.ToArray();
    }

    /// <summary>Writes the frame header for a reduced-still monochrome key frame with all features off. Mirrors
    /// Av1ObuParser.ParseFrameHeader. <paramref name="baseQIdx"/> must be non-zero (0 would flag lossless, which
    /// takes a different coding path). When <paramref name="isObuFrame"/> is true the header is only byte-aligned
    /// (tile data follows in the same OBU_FRAME); otherwise a trailing one-bit terminates a standalone
    /// OBU_FRAME_HEADER.</summary>
    internal static byte[] WriteFrameHeaderPayload(int baseQIdx, bool isObuFrame)
        => WriteFrameHeaderPayload(baseQIdx, isObuFrame, 1, 1, true, false);

    internal static byte[] WriteFrameHeaderPayload(int baseQIdx, bool isObuFrame, int sbCols, int sbRows)
        => WriteFrameHeaderPayload(baseQIdx, isObuFrame, sbCols, sbRows, true, false);

    internal static byte[] WriteFrameHeaderPayload(int baseQIdx, bool isObuFrame, int sbCols, int sbRows, bool monochrome)
        => WriteFrameHeaderPayload(baseQIdx, isObuFrame, sbCols, sbRows, monochrome, false);

    /// <summary>CDEF parameters for the frame header. Damping ∈ [3,6]; Bits ∈ [0,3] selects 1&lt;&lt;Bits strength
    /// sets. Each 6-bit strength packs (pri&lt;&lt;2)|sec. All-zero strengths ⇒ the decoder applies no filtering
    /// (a conformant no-op).</summary>
    internal readonly record struct CdefParams(int Damping, int Bits, byte[] YStrengths, byte[] UvStrengths)
    {
        internal static CdefParams None => new(3, 0, new byte[] { 0 }, new byte[] { 0 });
    }

    internal static byte[] WriteFrameHeaderPayload(int baseQIdx, bool isObuFrame, int sbCols, int sbRows, bool monochrome, bool txModeSelect)
        => WriteFrameHeaderPayload(baseQIdx, isObuFrame, sbCols, sbRows, monochrome, txModeSelect, CdefParams.None);

    /// <summary>Frame header for a frame that is <paramref name="sbCols"/> x <paramref name="sbRows"/> 64x64
    /// superblocks, coded as a single tile (uniform spacing, log2 tile dims 0). <paramref name="monochrome"/>
    /// selects whether the U/V quant-delta bits are emitted. <paramref name="txModeSelect"/> enables per-block
    /// tx-size signalling (TX_MODE_SELECT) — the encoder must then code a tx_depth symbol per block.</summary>
    internal static byte[] WriteFrameHeaderPayload(int baseQIdx, bool isObuFrame, int sbCols, int sbRows, bool monochrome, bool txModeSelect, CdefParams cdef, int lfLevel = 0, bool screenContentTools = false, bool reducedTxSet = true)
    {
        if (baseQIdx < 0 || baseQIdx > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(baseQIdx), "base_q_idx must be in [0,255].");
        }
        // base_q_idx 0 with every delta 0 is CodedLossless (and AllLossless: no superres): the loop filter, CDEF,
        // restoration and tx-mode fields are absent and every transform is a 4x4 WHT.
        bool lossless = baseQIdx == 0;

        var w = new Av1BitWriter();

        if (t_layered is { } layered)
        {
            WriteLayeredFramePrefix(w, layered, screenContentTools);
            goto tileInfo;
        }

        // reduced_still_picture_header ⇒ show_existing_frame / frame_type / show_frame / error_resilient are all
        // implied; nothing is written until here.
        w.PutBool(false);         // disable_cdf_update = 0 (adaptive CDFs; disable_frame_end_update_cdf inferred 1 in reduced still picture)
        w.PutBool(screenContentTools);  // allow_screen_content_tools (ScreenContentTools is Adaptive)
        if (screenContentTools)
            w.PutBool(false);     // force_integer_mv: seq force_integer_mv is Adaptive so the bit is read even
                                  // for intra (the decoder then overrides the value to 1); write 0.
        // frame_id absent; frame_size_override absent (reduced still).
        // primary_ref_frame implied NONE (intra); decoder model absent.
        // refresh_frame_flags = 0xFF implied (key + show_frame).

        // read_frame_size (useRef=false): frame_size_override=0 ⇒ dims from seq; superres off ⇒ no bit.
        w.PutBool(false);         // have_render_size = 0 (render size = frame size)
        if (screenContentTools)
            w.PutBool(false);     // allow_intra_bc = 0 (read for intra when screen tools on & !superres)
        // refresh_context skipped (reduced still).

    tileInfo:
        // tile_info (uniform spacing): the log2 tile counts start at the syntax minima and the loop reads an "increment"
        // bit while below the maximum — one 1 bit per step up to the chosen count (TileLayout), then a 0 unless already
        // at the maximum. Frames over 4096 px wide or 2304 superblocks always carry at least the tiles the decoder
        // expects.
        w.PutBool(true);          // uniform_tile_spacing_flag
        var layout = TileLayout(sbCols, sbRows);
        {
            const int maxTileWidthSb = 4096 >> 6, maxTileAreaSb = (4096 * 2304) >> 12;
            int minLog2Cols = TileLog2(maxTileWidthSb, sbCols);
            int minLog2Tiles = Math.Max(minLog2Cols, TileLog2(maxTileAreaSb, sbRows * sbCols));
            for (int i = minLog2Cols; i < layout.ColsLog2; i++) w.PutBool(true);      // increment_tile_cols_log2
            if (layout.ColsLog2 < TileMaxLog2(sbCols)) w.PutBool(false);
            for (int i = Math.Max(minLog2Tiles - layout.ColsLog2, 0); i < layout.RowsLog2; i++) w.PutBool(true);   // increment_tile_rows_log2
            if (layout.RowsLog2 < TileMaxLog2(sbRows)) w.PutBool(false);
        }
        if (layout.ColsLog2 + layout.RowsLog2 > 0)
        {
            w.PutBits(0, layout.ColsLog2 + layout.RowsLog2);   // context_update_tile_id = 0
            w.PutBits(3, 2);                                  // tile_size_bytes_minus_1 = 3 (4-byte tile sizes)
        }

        // quantization_params
        w.PutBits((uint)baseQIdx, 8); // base_q_idx
        w.PutBool(false);         // y_dc: delta_coded = 0
        if (!monochrome)
        {
            // separate_uv_delta_q = 0 ⇒ no diff_uv_delta bit; U deltas not coded (V = U).
            w.PutBool(false);     // u_dc_delta_coded = 0
            w.PutBool(false);     // u_ac_delta_coded = 0
        }

        w.PutBool(false);         // using_qmatrix = 0

        // segmentation_params
        w.PutBool(false);         // segmentation_enabled = 0

        // delta_q_params: delta_q_present is only coded when base_q_idx > 0.
        if (!lossless) w.PutBool(false);   // delta_q_present = 0
        if (lossless)
        {
            // loop_filter_params / cdef_params / lr_params / read_tx_mode are all skipped for a coded-lossless frame.
            bool interLl = t_layered is { Sequence: true, InterFrame: true };
            if (interLl) w.PutBool(t_layered!.ReferenceSelect);   // reference_select
            w.PutBool(reducedTxSet);  // reduced_tx_set
            if (interLl) for (int r = 0; r < 7; r++) w.PutBool(false);   // is_global
            WriteFilmGrainParams(w, monochrome, interLl);
            if (!isObuFrame) w.TrailingBits();
            return w.ToArray();
        }

        // loop_filter_params (not lossless, not intrabc). A single deblocking level is applied to both Y edges
        // (and, for colour, to U/V); level 0 = filter off. Deblocking is post-reconstruction and does not feed
        // intra prediction, so signalling a level never changes the coded tile — only the decoded output.
        uint lf = (uint)Math.Clamp(lfLevel, 0, 63);
        w.PutBits(lf, 6);         // loop_filter_level[0]
        w.PutBits(lf, 6);         // loop_filter_level[1]
        if (lf != 0 && !monochrome)
        {
            w.PutBits(lf, 6);     // loop_filter_level[2] (U) — read only when level[0]|level[1] and NumPlanes>1
            w.PutBits(lf, 6);     // loop_filter_level[3] (V)
        }
        w.PutBits(0, 3);          // loop_filter_sharpness = 0
        w.PutBool(false);         // loop_filter_delta_enabled = 0

        // cdef_params (enable_cdef=1, not lossless, not intrabc). Strengths may all be 0 = no-op filter.
        w.PutBits((uint)(cdef.Damping - 3), 2);   // cdef_damping_minus_3
        w.PutBits((uint)cdef.Bits, 2);            // cdef_bits
        for (int i = 0; i < (1 << cdef.Bits); i++)
        {
            w.PutBits(cdef.YStrengths[i], 6);     // cdef_y_strength (pri<<2 | sec)
            if (!monochrome)
            {
                w.PutBits(cdef.UvStrengths[i], 6); // cdef_uv_strength
            }
        }
        // lr_params skipped (enable_restoration=0)

        // read_tx_mode (not lossless)
        w.PutBool(txModeSelect);  // tx_mode_select: 0 ⇒ TX_MODE_LARGEST, 1 ⇒ TX_MODE_SELECT

        bool inter = t_layered is { Sequence: true, InterFrame: true };
        // frame_reference_mode: reference_select (inter frames); skip_mode / allow_warped_motion are absent (no order
        // hints, warped motion disabled).
        if (inter) w.PutBool(t_layered!.ReferenceSelect);   // reference_select: compound references allowed
        w.PutBool(reducedTxSet);  // reduced_tx_set (0 = full Intra1 set with V_DCT/H_DCT for sub-16x16 luma)

        // global_motion_params (inter frames): is_global = 0 for LAST..ALTREF.
        if (inter) for (int r = 0; r < 7; r++) w.PutBool(false);
        WriteFilmGrainParams(w, monochrome, inter);

        if (!isObuFrame)
        {
            w.TrailingBits();     // trailing_one_bit + byte alignment (OBU_FRAME_HEADER)
        }

        return w.ToArray();       // ToArray byte-aligns (OBU_FRAME: tile data follows the aligned header)
    }
}
