// AVIF (AV1 Image File Format) ISOBMFF container writer. AVIF is a HEIF/MIAF file whose primary item is an
// AV1-coded image: an `av01` item described by an `av1C` config box (carrying the sequence-header OBU) with the
// frame OBUs in `mdat`. Mirrors the box structure of the HEIC encoder, swapping hvc1/hvcC for av01/av1C.
// Optional: a monochrome alpha auxiliary item (auxl), Exif / XMP metadata items (cdsc), and primary-item
// properties for ICC (colr prof), transforms (clap/irot/imir, essential), pasp and HDR light levels (clli/mdcv),
// laid out the way libavif writes them. Verified: files open in ffmpeg/libdav1d and libavif.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Av1;

/// <summary>Optional AVIF container content beyond the coded image(s): colour profile, metadata items, transforms
/// and HDR properties of the primary item.</summary>
internal sealed class AvifContainerExtras
{
    public byte[]? Icc;                      // colr 'prof' (raw ICC profile)
    public byte[]? Exif;                     // Exif item payload (TIFF header onward, or with a leading "Exif\0\0")
    public byte[]? Xmp;                      // XMP packet (mime item, application/rdf+xml)
    public int? IrotAngle;                   // irot: anti-clockwise rotation in 90° units (0..3)
    public int? ImirAxis;                    // imir (ISO/IEC 23008-12:2022): 0 = top/bottom exchanged, 1 = left/right exchanged
    public uint[]? Clap;                     // clap: widthN,widthD,heightN,heightD,horizOffN,horizOffD,vertOffN,vertOffD
    public (uint H, uint V)? Pasp;           // pasp: pixel aspect ratio hSpacing:vSpacing
    public (ushort MaxCll, ushort MaxPall)? Clli;
    public byte[]? Mdcv;                     // mdcv payload (24 bytes: display primaries, white point, max/min luminance)
    public bool Premultiplied;               // colour is premultiplied by alpha: iref 'prem' colour -> alpha
    public long[]? ColorLayerSizes;          // layered (progressive) colour item: byte size of each layer -> 'a1lx'
    public long[]? AlphaLayerSizes;          // layered alpha item

    public AvifSequenceData? Sequence;       // image sequence (animated AVIF): tracks written after the items
    public AvifGainMapItem? GainMap;         // ISO 21496-1 gain map: 'tmap' derived item + hidden gain map image item
    public AvifSampleTransformItems? SampleTransform;   // 'sato' bit-depth extension: derived item + hidden input items

    internal bool HasItems => Exif != null || Xmp != null;
}

/// <summary>An AVIF image sequence's coded samples (one temporal unit per frame, colour and optional alpha) and timing.
/// The first samples double as the primary colour / alpha items.</summary>
/// <summary>A coded gain map and its 'tmap' item, laid out as libavif writes them: the tmap item (payload = the
/// ToneMapImage box contents) with ispe / alternate pixi / alternate colr / alternate clli, preferred over the colour item
/// in an 'altr' group and deriving from [colour, gain map] ('dimg'); the gain map a hidden av01 item with its own
/// ispe / pixi / av1C / colr nclx, plus the colour item's pasp and transforms.</summary>
internal sealed class AvifGainMapItem
{
    public byte[] Data = [];                 // coded gain map (temporal unit)
    public int Width, Height;
    public byte[] PixiBox = [], Av1CBox = [], ColrBox = [];
    public byte[] Tmap = [];                 // tmap item payload
    public byte[]? AltPixiBox, AltIccBox, AltClliBox;
    public byte[] AltNclxBox = [];
}

/// <summary>The coded cells of a grid image ('grid' derived item, ISO/IEC 23008-12 6.6.2.3): row-major colour cells and,
/// when present, alpha cells, with the configuration boxes of the first cell (shared by every cell, as libavif writes
/// them — the last row / column may be coded smaller).</summary>
internal sealed class AvifGridCells
{
    public int Columns, Rows, CellWidth, CellHeight;
    public List<byte[]> Color = [];
    public List<byte[]>? Alpha;
    public byte[] ColorAv1CBox = [], AlphaAv1CBox = [];
    // Layered (progressive) cells: each cell's layer sizes -> its own 'a1lx' and one iloc extent per layer (libavif's
    // layered grid, avifEncoderAddImageGrid with extraLayerCount).
    public List<long[]>? ColorLayerSizes, AlphaLayerSizes;
}

/// <summary>A 'sato' Sample Transform derived item (libavif bit-depth extension) and its hidden second input items, laid
/// out as libavif writes them: the sato item (payload = SampleTransform box contents; ispe, pixi at the extended depth)
/// preferred over the colour item in an 'altr' group and deriving from [colour, hidden colour] ('dimg'); the hidden
/// colour item (ispe / pixi / av1C) with, for translucent images, a hidden alpha item (auxl to the hidden colour).</summary>
internal sealed class AvifSampleTransformItems
{
    public byte[] Payload = [];
    public int Depth = 16;
    public byte[] HiddenColor = [];
    public int HiddenDepth = 8;
    public byte[] HiddenColorAv1CBox = [];
    public byte[]? HiddenAlpha;
    public byte[] HiddenAlphaAv1CBox = [];
}

internal sealed class AvifSequenceData
{
    public List<byte[]> ColorSamples = [];
    public List<byte[]>? AlphaSamples;
    public uint[] Durations = [];            // per frame, in Timescale units
    public uint Timescale = 30;
    public ulong CreationTime, ModificationTime;   // seconds since 1904-01-01 UTC (mvhd / tkhd / mdhd)
    public int RepetitionCount;              // extra plays; -1 = infinite (libavif AVIF_REPETITION_COUNT_INFINITE)
    public bool AllKeyFrames = true;         // every sample a sync (key) frame: no 'stss', all-intra 'ccst'
    // Per-sample sync (key frame) flags of each track when some samples are inter frames (null = all sync). A track
    // with non-sync samples gets an 'stss' listing its sync samples and a 'ccst' allowing inter prediction; the file
    // gets the 'avio' brand when some track is all sync (libavif write.c).
    public List<bool>? ColorSync, AlphaSync;
    public bool ColorAllSync => ColorSync == null || ColorSync.TrueForAll(v => v);
    public bool AlphaAllSync => AlphaSync == null || AlphaSync.TrueForAll(v => v);
}

internal static class Av1AvifWriter
{
    /// <summary>Builds a complete .avif file. <paramref name="seqObu"/> is the sequence-header OBU (size-field
    /// form); it is placed in-band at the head of the mdat item data (the item decodes as a self-contained temporal
    /// unit, as libaom/ffmpeg AVIF output does). <paramref name="frameObu"/> is the OBU_FRAME.</summary>
    internal static byte[] BuildAvif(byte[] seqObu, byte[] frameObu, int width, int height, bool monochrome, int bitDepth = 8,
        Av1PixelLayout layout = Av1PixelLayout.I420, Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        if (monochrome) layout = Av1PixelLayout.I400;
        return BuildContainer(Concat(seqObu, frameObu), null, width, height, bitDepth, layout, color, extras);
    }

    /// <summary>Builds a 2-item AVIF: a primary colour `av01` item (item 1) and a monochrome alpha auxiliary
    /// `av01` item (item 2) linked by an `auxl` item reference (item 2 → item 1) with the standard alpha aux URN.
    /// Both items' OBUs share one mdat (colour first, then alpha) as two extents. Verified in ffmpeg/libavif.</summary>
    internal static byte[] BuildAvifWithAlpha(byte[] colorSeq, byte[] colorFrame, byte[] alphaSeq, byte[] alphaFrame,
        int width, int height, bool colorMonochrome, int bitDepth = 8, Av1PixelLayout layout = Av1PixelLayout.I420,
        Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        if (colorMonochrome) layout = Av1PixelLayout.I400;
        return BuildContainer(Concat(colorSeq, colorFrame), Concat(alphaSeq, alphaFrame), width, height, bitDepth, layout, color, extras);
    }

    // colr nclx. Legacy (no description given): unspecified primaries/transfer, matrix Identity(0) for mono /
    // BT.601(6) for colour, full range. Otherwise the exact CICP + full_range_flag (must match the sequence header).
    private static byte[] ColrNclx(Av1ObuWriter.Av1ColorDesc? color, bool monochrome)
        => color is { } c
            ? Box("colr", Concat(Fourcc("nclx"), U16((ushort)c.Primaries), U16((ushort)c.Transfer), U16((ushort)c.Matrix),
                new byte[] { (byte)(c.FullRange ? 0x80 : 0x00) }))
            : Box("colr", Concat(Fourcc("nclx"), U16(2), U16(2), U16(monochrome ? 0 : 6), new byte[] { 0x80 }));

    // AV1CodecConfigurationRecord (av1C payload): the fixed 4-byte record only (configOBUs omitted, as libaom's
    // AVIF output does — the sequence header travels in-band in mdat).
    internal static byte[] BuildAv1C(Av1PixelLayout layout, int bitDepth, int width, int height)
    {
        // seq_level_idx_0 = 0 (matches Av1ObuWriter's reduced-still header), tier 0. seq_profile / high_bitdepth /
        // twelve_bit / subsampling mirror the sequence header's color_config.
        int profile = Av1ObuWriter.SeqProfile(bitDepth, layout);
        byte b0 = 0x81;                       // marker(1)=1 | version(7)=1
        byte b1 = (byte)((profile << 5) | Av1ObuWriter.SeqLevelIdx(width, height));   // seq_profile(3) | seq_level_idx_0(5)
        int monoBit = layout == Av1PixelLayout.I400 ? 1 : 0;
        // AV1 sets subsampling 1,1 for monochrome (I400) and 4:2:0; 4:2:2 is 1,0; 4:4:4 is 0,0.
        int cssX = layout == Av1PixelLayout.I444 ? 0 : 1;
        int cssY = layout == Av1PixelLayout.I422 || layout == Av1PixelLayout.I444 ? 0 : 1;
        byte b2 = (byte)(
            (0 << 7) |                        // seq_tier_0
            ((bitDepth > 8 ? 1 : 0) << 6) |   // high_bitdepth
            ((bitDepth == 12 ? 1 : 0) << 5) | // twelve_bit
            (monoBit << 4) |                  // monochrome
            (cssX << 3) |                     // chroma_subsampling_x
            (cssY << 2) |                     // chroma_subsampling_y
            (layout == Av1PixelLayout.I420 ? Av1ObuWriter.ChromaSamplePosition : 0));   // chroma_sample_position (2 bits)
        byte b3 = 0x00;                       // reserved(3)=0 | initial_presentation_delay_present(1)=0 | reserved(4)=0

        return new[] { b0, b1, b2, b3 };
    }

    // ftyp: major brand avif; compatible avif/mif1/miaf plus the AVIF profile brand the stream qualifies for —
    // MA1B (Baseline = AV1 Main profile), MA1A (Advanced = AV1 High profile, i.e. 8/10-bit 4:4:4); AV1
    // Professional streams (4:2:2, 12-bit) fit no AVIF profile brand, so none is claimed (as libavif does).
    private static byte[] Ftyp(int bitDepth, Av1PixelLayout layout, bool sequence = false, bool toneMapped = false, bool avio = false)
    {
        int profile = Av1ObuWriter.SeqProfile(bitDepth, layout);
        // An image sequence (libavif): major brand 'avis', compatible avif, [avio: some track made only of sync
        // samples], avis, msf1, iso8, mif1, miaf.
        var brands = sequence
            ? Concat(Fourcc("avis"), U32(0), Fourcc("avif"), avio ? Fourcc("avio") : [], Fourcc("avis"), Fourcc("msf1"), Fourcc("iso8"),
                Fourcc("mif1"), Fourcc("miaf"))
            : Concat(Fourcc("avif"), U32(0), Fourcc("avif"), Fourcc("mif1"), Fourcc("miaf"));
        brands = profile switch
        {
            0 => Concat(brands, Fourcc("MA1B")),
            1 => Concat(brands, Fourcc("MA1A")),
            _ => brands,
        };
        // 'tmap' (ISO/IEC 23008-12:2024/AMD 1): readers only consider a tone-mapped derived image with this brand.
        return Box("ftyp", toneMapped ? Concat(brands, Fourcc("tmap")) : brands);
    }

    // One container builder for every layout. Item IDs: 1 = colour (primary), 2 = alpha (if any), then Exif, XMP.
    // Property order: ispe, pixi, av1C, [colr prof], colr nclx, [pasp, clli, mdcv, clap, irot, imir], then the alpha
    // item's av1C / auxC / pixi. With no extras this reproduces the original single/2-item byte layout exactly.
    // AV1LayeredImageIndexingProperty: the sizes of all layers but the last (libavif writes it non-essential and, like
    // it, no 'lsel', so the default presentation is the full image and progressive readers get every layer).
    // One iloc entry (version 0, 4-byte offsets / lengths, no base offset): a single extent, or one extent per layer of
    // a layered item as libavif writes it.
    private static byte[] IlocEntry(int id, uint offset, int length, long[]? layerSizes)
    {
        if (layerSizes is not { Length: > 1 } || layerSizes.Sum() != length)
            return Concat(U16(id), U16(0), U16(1), U32(offset), U32((uint)length));
        var parts = new List<byte[]> { U16(id), U16(0), U16(layerSizes.Length) };
        uint o = offset;
        foreach (long sz in layerSizes)
        {
            parts.Add(U32(o));
            parts.Add(U32((uint)sz));
            o += (uint)sz;
        }
        return Concat(parts.ToArray());
    }

    private static byte[] A1lx(long[] sizes)
    {
        bool large = false;
        for (int i = 0; i < sizes.Length - 1; i++) large |= sizes[i] > 0xFFFF;
        var b = new List<byte> { (byte)(large ? 1 : 0) };
        for (int i = 0; i < 3; i++)
        {
            long s = i < sizes.Length - 1 ? sizes[i] : 0;
            if (large) b.AddRange(U32((uint)s));
            else b.AddRange(U16((ushort)s));
        }
        return Box("a1lx", b.ToArray());
    }

    private static byte[] BuildContainer(byte[] colorData, byte[]? alphaData, int width, int height, int bitDepth,
        Av1PixelLayout layout, Av1ObuWriter.Av1ColorDesc? color, AvifContainerExtras? x)
    {
        bool monochrome = layout == Av1PixelLayout.I400;
        var sq = x?.Sequence;
        var gmx = x?.GainMap;
        var stx = x?.SampleTransform;
        if (gmx != null && stx != null) throw new NotSupportedException("A gain map and a Sample Transform cannot share the 'altr' group.");
        byte[] ftyp = Ftyp(bitDepth, layout, sq != null, gmx != null,
            avio: sq != null && (sq.ColorAllSync || (sq.AlphaSamples != null && sq.AlphaAllSync)));

        var props = new List<byte[]>();
        int Add(byte[] box) { props.Add(box); return props.Count; }
        // Gain map properties are deduplicated against the existing ones, as libavif does.
        int AddShared(byte[] box)
        {
            for (int i = 0; i < props.Count; i++) if (props[i].AsSpan().SequenceEqual(box)) return i + 1;
            return Add(box);
        }
        var assoc1 = new List<(int Index, bool Essential)>();
        int ispeIdx = Add(FullBox("ispe", 0, 0, Concat(U32((uint)width), U32((uint)height))));
        assoc1.Add((ispeIdx, false));
        int ch = monochrome ? 1 : 3;
        var pixi = new byte[1 + ch];
        pixi[0] = (byte)ch;
        for (int i = 1; i <= ch; i++) pixi[i] = (byte)bitDepth;
        assoc1.Add((Add(FullBox("pixi", 0, 0, pixi)), false));
        assoc1.Add((Add(Box("av1C", BuildAv1C(layout, bitDepth, width, height))), true));
        if (x?.Icc is { Length: > 0 } icc) assoc1.Add((Add(Box("colr", Concat(Fourcc("prof"), icc))), false));
        assoc1.Add((Add(ColrNclx(color, monochrome)), false));
        if (x?.Pasp is { } pasp) assoc1.Add((Add(Box("pasp", Concat(U32(pasp.H), U32(pasp.V)))), false));
        if (x?.Clli is { } clli) assoc1.Add((Add(Box("clli", Concat(U16(clli.MaxCll), U16(clli.MaxPall)))), false));
        if (x?.Mdcv is { Length: 24 } mdcv) assoc1.Add((Add(Box("mdcv", mdcv)), false));
        if (x?.ColorLayerSizes is { Length: > 1 } cls) assoc1.Add((Add(A1lx(cls)), false));
        var transforms = new List<(int Index, bool Essential)>();   // shared with the alpha item (libavif >= 1.3)
        if (x?.Clap is { Length: 8 } clap)
        {
            var cb = new byte[32];
            for (int i = 0; i < 8; i++) WriteU32(cb, i * 4, clap[i]);
            transforms.Add((Add(Box("clap", cb)), true));
        }
        if (x?.IrotAngle is { } angle) transforms.Add((Add(Box("irot", new[] { (byte)(angle & 3) })), true));
        if (x?.ImirAxis is { } axis) transforms.Add((Add(Box("imir", new[] { (byte)(axis & 1) })), true));
        assoc1.AddRange(transforms);

        var assoc2 = new List<(int Index, bool Essential)>();
        if (alphaData != null)
        {
            assoc2.Add((ispeIdx, false));
            assoc2.Add((Add(Box("av1C", BuildAv1C(Av1PixelLayout.I400, bitDepth, width, height))), true));
            // auxC: aux_type is a null-terminated URN string identifying the alpha plane.
            byte[] auxUrn = System.Text.Encoding.ASCII.GetBytes("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha\0");
            assoc2.Add((Add(FullBox("auxC", 0, 0, auxUrn)), true));
            assoc2.Add((Add(FullBox("pixi", 0, 0, new byte[] { 1, (byte)bitDepth })), false));
            if (x?.AlphaLayerSizes is { Length: > 1 } als) assoc2.Add((Add(A1lx(als)), false));
            assoc2.AddRange(transforms);   // the alpha plane is transformed exactly like the colour image
        }

        var assocTmap = new List<(int Index, bool Essential)>();
        var assocGm = new List<(int Index, bool Essential)>();
        if (gmx != null)
        {
            assocTmap.Add((ispeIdx, false));
            if (gmx.AltPixiBox != null) assocTmap.Add((AddShared(gmx.AltPixiBox), false));
            if (gmx.AltIccBox != null) assocTmap.Add((AddShared(gmx.AltIccBox), false));
            assocTmap.Add((AddShared(gmx.AltNclxBox), false));
            if (gmx.AltClliBox != null) assocTmap.Add((AddShared(gmx.AltClliBox), false));

            assocGm.Add((AddShared(FullBox("ispe", 0, 0, Concat(U32((uint)gmx.Width), U32((uint)gmx.Height)))), false));
            assocGm.Add((AddShared(gmx.PixiBox), false));
            assocGm.Add((AddShared(gmx.Av1CBox), true));
            assocGm.Add((AddShared(gmx.ColrBox), false));
            if (x?.Pasp is { } gpasp) assocGm.Add((AddShared(Box("pasp", Concat(U32(gpasp.H), U32(gpasp.V)))), false));
            if (x?.Clap != null && (gmx.Width != width || gmx.Height != height))
                throw new NotSupportedException("A clean aperture cannot be applied to a gain map of another size.");
            assocGm.AddRange(transforms);  // the gain map is transformed exactly like the colour image
        }

        var assocSato = new List<(int Index, bool Essential)>();
        var assocHidden = new List<(int Index, bool Essential)>();
        var assocHiddenAlpha = new List<(int Index, bool Essential)>();
        if (stx != null)
        {
            int chs = monochrome ? 1 : 3;
            var sp = new byte[1 + chs];
            sp[0] = (byte)chs;
            for (int i = 1; i <= chs; i++) sp[i] = (byte)stx.Depth;
            assocSato.Add((ispeIdx, false));
            assocSato.Add((AddShared(FullBox("pixi", 0, 0, sp)), false));
            var hp = new byte[1 + chs];
            hp[0] = (byte)chs;
            for (int i = 1; i <= chs; i++) hp[i] = (byte)stx.HiddenDepth;
            assocHidden.Add((ispeIdx, false));
            assocHidden.Add((AddShared(FullBox("pixi", 0, 0, hp)), false));
            assocHidden.Add((AddShared(stx.HiddenColorAv1CBox), true));
            if (stx.HiddenAlpha != null)
            {
                assocHiddenAlpha.Add((ispeIdx, false));
                assocHiddenAlpha.Add((AddShared(stx.HiddenAlphaAv1CBox), true));
                byte[] urn = System.Text.Encoding.ASCII.GetBytes("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha\0");
                assocHiddenAlpha.Add((AddShared(FullBox("auxC", 0, 0, urn)), true));
                assocHiddenAlpha.Add((AddShared(FullBox("pixi", 0, 0, new byte[] { 1, (byte)stx.HiddenDepth })), false));
            }
        }

        // Items: (id, type, payload, infe name / content type, infe flags; 1 = hidden).
        var items = new List<(int Id, string Type, byte[] Payload, byte[] InfeExtra, uint Flags)>
        {
            (1, "av01", colorData, new byte[] { 0 }, 0),
        };
        if (alphaData != null) items.Add((2, "av01", alphaData, new byte[] { 0 }, 0));
        int nextId = items.Count + 1;
        int tmapId = 0, gmId = 0;
        if (gmx != null)
        {
            tmapId = nextId++;
            items.Add((tmapId, "tmap", gmx.Tmap, new byte[] { 0 }, 0));
            gmId = nextId++;
            items.Add((gmId, "av01", gmx.Data, new byte[] { 0 }, 1));
        }
        int satoId = 0, hiddenId = 0, hiddenAlphaId = 0;
        if (stx != null)
        {
            satoId = nextId++;
            items.Add((satoId, "sato", stx.Payload, new byte[] { 0 }, 0));
            hiddenId = nextId++;
            items.Add((hiddenId, "av01", stx.HiddenColor, new byte[] { 0 }, 1));
            if (stx.HiddenAlpha != null)
            {
                hiddenAlphaId = nextId++;
                items.Add((hiddenAlphaId, "av01", stx.HiddenAlpha, new byte[] { 0 }, 0));
            }
        }
        int exifId = 0, xmpId = 0;
        if (x?.Exif is { Length: > 0 } exif)
        {
            exifId = nextId++;
            items.Add((exifId, "Exif", ExifItemPayload(exif), System.Text.Encoding.ASCII.GetBytes("Exif\0"), 0));
        }
        if (x?.Xmp is { Length: > 0 } xmp)
        {
            xmpId = nextId++;
            items.Add((xmpId, "mime", xmp, System.Text.Encoding.ASCII.GetBytes("XMP\0application/rdf+xml\0"), 0));
        }

        byte[] ipco = Box("ipco", Concat(props.ToArray()));
        var ipmaBody = new List<byte[]> { U32((uint)((alphaData != null ? 2 : 1) + (gmx != null ? 2 : 0)
            + (stx != null ? (stx.HiddenAlpha != null ? 3 : 2) : 0))) };
        void Assoc(int id, List<(int Index, bool Essential)> a)
        {
            ipmaBody.Add(U16(id));
            ipmaBody.Add(new[] { (byte)a.Count });
            foreach (var (idx, ess) in a) ipmaBody.Add(new[] { (byte)((ess ? 0x80 : 0) | idx) });
        }
        Assoc(1, assoc1);
        if (alphaData != null) Assoc(2, assoc2);
        if (gmx != null) { Assoc(tmapId, assocTmap); Assoc(gmId, assocGm); }
        if (stx != null)
        {
            Assoc(satoId, assocSato);
            Assoc(hiddenId, assocHidden);
            if (hiddenAlphaId != 0) Assoc(hiddenAlphaId, assocHiddenAlpha);
        }
        byte[] iprp = Box("iprp", Concat(ipco, FullBox("ipma", 0, 0, Concat(ipmaBody.ToArray()))));

        byte[] hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Fourcc("pict"), U32(0), U32(0), U32(0),
            new byte[] { 0 }));   // empty handler name, as libavif (14 bytes less per file)
        byte[] pitm = FullBox("pitm", 0, 0, U16(1));
        var infes = new List<byte[]> { U16(items.Count) };
        foreach (var it in items) infes.Add(FullBox("infe", 2, it.Flags, Concat(U16(it.Id), U16(0), Fourcc(it.Type), it.InfeExtra)));
        byte[] iinf = FullBox("iinf", 0, 0, Concat(infes.ToArray()));

        // iref (version 0): auxl alpha → colour; cdsc metadata → colour.
        var refs = new List<byte[]>();
        if (alphaData != null) refs.Add(Box("auxl", Concat(U16(2), U16(1), U16(1))));   // from_ID, ref_count, to_ID
        if (alphaData != null && x?.Premultiplied == true) refs.Add(Box("prem", Concat(U16(1), U16(1), U16(2))));   // colour premultiplied by alpha
        if (gmx != null) refs.Add(Box("dimg", Concat(U16(tmapId), U16(2), U16(1), U16(gmId))));   // tmap from [colour, gain map]
        if (stx != null)
        {
            refs.Add(Box("dimg", Concat(U16(satoId), U16(2), U16(1), U16(hiddenId))));   // sato from [colour, hidden colour]
            if (hiddenAlphaId != 0)
            {
                refs.Add(Box("auxl", Concat(U16(hiddenAlphaId), U16(1), U16(hiddenId))));
                if (x?.Premultiplied == true) refs.Add(Box("prem", Concat(U16(hiddenId), U16(1), U16(hiddenAlphaId))));
            }
        }
        if (exifId != 0) refs.Add(Box("cdsc", Concat(U16(exifId), U16(1), U16(1))));
        if (xmpId != 0) refs.Add(Box("cdsc", Concat(U16(xmpId), U16(1), U16(1))));
        byte[]? iref = refs.Count > 0 ? FullBox("iref", 0, 0, Concat(refs.ToArray())) : null;

        // mdat: for a sequence, the colour then alpha samples (one chunk each; the first samples are the colour / alpha
        // items' data, as in libavif), then the other items; otherwise every item's payload in order.
        var chunks = new List<byte[]>();
        var itemOffset = new long[items.Count];   // relative to the mdat payload start
        long colorChunk = 0, alphaChunk = 0, pos = 0;
        if (sq != null)
        {
            colorChunk = pos;
            foreach (var smp in sq.ColorSamples) { chunks.Add(smp); pos += smp.Length; }
            if (sq.AlphaSamples != null)
            {
                alphaChunk = pos;
                foreach (var smp in sq.AlphaSamples) { chunks.Add(smp); pos += smp.Length; }
            }
        }
        for (int i = 0; i < items.Count; i++)
        {
            if (sq != null && i == 0) { itemOffset[i] = colorChunk; continue; }
            if (sq != null && i == 1 && alphaData != null) { itemOffset[i] = alphaChunk; continue; }
            itemOffset[i] = pos;
            chunks.Add(items[i].Payload);
            pos += items[i].Payload.Length;
        }

        // iloc: version 0, offset_size=4/length_size=4/base_offset_size=0; one extent per item, or per layer of a layered
        // colour / alpha item. Offsets are patched once the meta (and moov) lengths (invariant to the offset values) are
        // known.
        byte[] Iloc(uint mdatStart)
        {
            var body = new List<byte[]> { new byte[] { 0x44, 0x00 }, U16(items.Count) };
            for (int i = 0; i < items.Count; i++)
            {
                long[]? layers = items[i].Id == 1 ? x?.ColorLayerSizes : items[i].Id == 2 && alphaData != null ? x?.AlphaLayerSizes : null;
                body.Add(IlocEntry(items[i].Id, (uint)(mdatStart + itemOffset[i]), items[i].Payload.Length, layers));
            }
            return FullBox("iloc", 0, 0, Concat(body.ToArray()));
        }

        // grpl: the tmap item is the preferred alternative to the colour item ('altr', tmap first); the group id must
        // not collide with an item id.
        byte[] grpl = gmx != null
            ? Box("grpl", FullBox("altr", 0, 0, Concat(U32((uint)nextId), U32(2), U32((uint)tmapId), U32(1))))
            : stx != null
            ? Box("grpl", FullBox("altr", 0, 0, Concat(U32((uint)nextId), U32(2), U32((uint)satoId), U32(1))))
            : [];

        byte[] Meta(uint mdatStart) => FullBox("meta", 0, 0, iref != null
            ? Concat(hdlr, pitm, Iloc(mdatStart), iinf, iref, iprp, grpl)
            : Concat(hdlr, pitm, Iloc(mdatStart), iinf, iprp, grpl));

        int metaLen = Meta(0).Length;
        if (sq == null)
        {
            byte[] meta = Meta((uint)(ftyp.Length + metaLen + 8));   // + 8: mdat box header
            return Concat(ftyp, meta, Box("mdat", Concat(chunks.ToArray())));
        }
        var colorEntry = SampleEntryChildren(Box("av1C", BuildAv1C(layout, bitDepth, width, height)), x, color, monochrome, alpha: false,
            sq.AllKeyFrames && sq.ColorAllSync);
        byte[]? alphaEntry = sq.AlphaSamples != null
            ? SampleEntryChildren(Box("av1C", BuildAv1C(Av1PixelLayout.I400, bitDepth, width, height)), null, null, true, alpha: true,
                sq.AllKeyFrames && sq.AlphaAllSync)
            : null;
        int moovLen = Moov(sq, width, height, colorEntry, alphaEntry, 0, 0, x?.Premultiplied == true).Length;
        uint start = (uint)(ftyp.Length + metaLen + moovLen + 8);
        return Concat(ftyp, Meta(start), Moov(sq, width, height, colorEntry, alphaEntry, start + (uint)colorChunk, start + (uint)alphaChunk,
            x?.Premultiplied == true), Box("mdat", Concat(chunks.ToArray())));
    }

    /// <summary>
    /// A grid image as libavif writes it: item 1 = the colour 'grid' (payload ImageGrid; ispe of the whole image, pixi,
    /// colr, pasp / clli / mdcv, transforms), its cells as hidden av01 items (ispe of the first cell, pixi, av1C, the same
    /// colr / pasp / clli / mdcv); then an alpha 'grid' (auxC, auxl to the colour grid) with hidden alpha cells; then an
    /// optional tone-mapped gain map ('tmap' over [colour grid, gain map]) and Exif / XMP. Transforms (clap / irot /
    /// imir) sit on the grid items only: HEIF applies an input image's transforms before composing the grid, so they must
    /// not be on the cells (libavif also stamps them on cells and ignores them there when reading).
    /// </summary>
    internal static byte[] BuildGridAvif(AvifGridCells g, int width, int height, int bitDepth, Av1PixelLayout layout,
        Av1ObuWriter.Av1ColorDesc? color, AvifContainerExtras? x)
    {
        bool monochrome = layout == Av1PixelLayout.I400;
        var gmx = x?.GainMap;
        bool hasAlpha = g.Alpha != null;
        byte[] ftyp = Ftyp(bitDepth, layout, false, gmx != null);

        var props = new List<byte[]>();
        int Add(byte[] box)
        {
            for (int i = 0; i < props.Count; i++) if (props[i].AsSpan().SequenceEqual(box)) return i + 1;
            props.Add(box);
            return props.Count;
        }

        // ImageGrid payload: version 0, flags (1 = 32-bit output size), rows_minus_one, columns_minus_one, size.
        bool large = width > 65535 || height > 65535;
        byte[] gridPayload = Concat(new byte[] { 0, (byte)(large ? 1 : 0), (byte)(g.Rows - 1), (byte)(g.Columns - 1) },
            large ? Concat(U32((uint)width), U32((uint)height)) : Concat(U16(width), U16(height)));

        int ch = monochrome ? 1 : 3;
        var pixiBytes = new byte[1 + ch];
        pixiBytes[0] = (byte)ch;
        for (int i = 1; i <= ch; i++) pixiBytes[i] = (byte)bitDepth;

        // Colour grid.
        var gridAssoc = new List<(int Index, bool Essential)>();
        int ispeFull = Add(FullBox("ispe", 0, 0, Concat(U32((uint)width), U32((uint)height))));
        gridAssoc.Add((ispeFull, false));
        int pixiIdx = Add(FullBox("pixi", 0, 0, pixiBytes));
        gridAssoc.Add((pixiIdx, false));
        var colourProps = new List<(int Index, bool Essential)>();
        if (x?.Icc is { Length: > 0 } icc) colourProps.Add((Add(Box("colr", Concat(Fourcc("prof"), icc))), false));
        colourProps.Add((Add(ColrNclx(color, monochrome)), false));
        if (x?.Pasp is { } pasp) colourProps.Add((Add(Box("pasp", Concat(U32(pasp.H), U32(pasp.V)))), false));
        if (x?.Clli is { } clli) colourProps.Add((Add(Box("clli", Concat(U16(clli.MaxCll), U16(clli.MaxPall)))), false));
        if (x?.Mdcv is { Length: 24 } mdcv) colourProps.Add((Add(Box("mdcv", mdcv)), false));
        gridAssoc.AddRange(colourProps);
        var transforms = new List<(int Index, bool Essential)>();
        if (x?.Clap is { Length: 8 } clap)
        {
            var cb = new byte[32];
            for (int i = 0; i < 8; i++) WriteU32(cb, i * 4, clap[i]);
            transforms.Add((Add(Box("clap", cb)), true));
        }
        if (x?.IrotAngle is { } angle) transforms.Add((Add(Box("irot", new[] { (byte)(angle & 3) })), true));
        if (x?.ImirAxis is { } axis) transforms.Add((Add(Box("imir", new[] { (byte)(axis & 1) })), true));
        gridAssoc.AddRange(transforms);

        // Colour cells.
        var cellAssoc = new List<(int Index, bool Essential)>();
        int ispeCell = Add(FullBox("ispe", 0, 0, Concat(U32((uint)g.CellWidth), U32((uint)g.CellHeight))));
        cellAssoc.Add((ispeCell, false));
        cellAssoc.Add((pixiIdx, false));
        cellAssoc.Add((Add(g.ColorAv1CBox), true));
        cellAssoc.AddRange(colourProps);

        // Alpha grid + cells.
        byte[] auxUrn = System.Text.Encoding.ASCII.GetBytes("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha\0");
        var alphaGridAssoc = new List<(int Index, bool Essential)>();
        var alphaCellAssoc = new List<(int Index, bool Essential)>();
        if (hasAlpha)
        {
            int aPixi = Add(FullBox("pixi", 0, 0, new byte[] { 1, (byte)bitDepth }));
            int auxC = Add(FullBox("auxC", 0, 0, auxUrn));
            alphaGridAssoc.Add((ispeFull, false));
            alphaGridAssoc.Add((aPixi, false));
            alphaGridAssoc.Add((auxC, true));
            alphaGridAssoc.AddRange(transforms);
            alphaCellAssoc.Add((ispeCell, false));
            alphaCellAssoc.Add((aPixi, false));
            alphaCellAssoc.Add((Add(g.AlphaAv1CBox), true));
            alphaCellAssoc.Add((auxC, true));
        }

        // Gain map (a single coded item even under a grid base: the tmap derives from [colour grid, gain map]).
        var assocTmap = new List<(int Index, bool Essential)>();
        var assocGm = new List<(int Index, bool Essential)>();
        if (gmx != null)
        {
            assocTmap.Add((ispeFull, false));
            if (gmx.AltPixiBox != null) assocTmap.Add((Add(gmx.AltPixiBox), false));
            if (gmx.AltIccBox != null) assocTmap.Add((Add(gmx.AltIccBox), false));
            assocTmap.Add((Add(gmx.AltNclxBox), false));
            if (gmx.AltClliBox != null) assocTmap.Add((Add(gmx.AltClliBox), false));
            assocGm.Add((Add(FullBox("ispe", 0, 0, Concat(U32((uint)gmx.Width), U32((uint)gmx.Height)))), false));
            assocGm.Add((Add(gmx.PixiBox), false));
            assocGm.Add((Add(gmx.Av1CBox), true));
            assocGm.Add((Add(gmx.ColrBox), false));
            if (x?.Pasp is { } gpasp) assocGm.Add((Add(Box("pasp", Concat(U32(gpasp.H), U32(gpasp.V)))), false));
            if (x?.Clap != null && (gmx.Width != width || gmx.Height != height))
                throw new NotSupportedException("A clean aperture cannot be applied to a gain map of another size.");
            assocGm.AddRange(transforms);
        }

        // Items.
        var items = new List<(int Id, string Type, byte[] Payload, uint Flags, List<(int Index, bool Essential)>? Assoc)>();
        int nextId = 1;
        int gridId = nextId++;
        items.Add((gridId, "grid", gridPayload, 0, gridAssoc));
        var cellIds = new List<int>();
        var layerSizesById = new Dictionary<int, long[]>();
        List<(int Index, bool Essential)> CellAssoc(List<(int Index, bool Essential)> shared, long[]? layers)
        {
            if (layers is not { Length: > 1 }) return shared;
            return [.. shared, (Add(A1lx(layers)), false)];
        }
        for (int ci = 0; ci < g.Color.Count; ci++)
        {
            int id = nextId++;
            cellIds.Add(id);
            long[]? layers = g.ColorLayerSizes?[ci];
            if (layers != null) layerSizesById[id] = layers;
            items.Add((id, "av01", g.Color[ci], 1, CellAssoc(cellAssoc, layers)));
        }
        int alphaGridId = 0;
        var alphaCellIds = new List<int>();
        if (hasAlpha)
        {
            alphaGridId = nextId++;
            items.Add((alphaGridId, "grid", gridPayload, 0, alphaGridAssoc));
            for (int ci = 0; ci < g.Alpha!.Count; ci++)
            {
                int id = nextId++;
                alphaCellIds.Add(id);
                long[]? layers = g.AlphaLayerSizes?[ci];
                if (layers != null) layerSizesById[id] = layers;
                items.Add((id, "av01", g.Alpha[ci], 1, CellAssoc(alphaCellAssoc, layers)));
            }
        }
        int tmapId = 0, gmId = 0;
        if (gmx != null)
        {
            tmapId = nextId++;
            items.Add((tmapId, "tmap", gmx.Tmap, 0, assocTmap));
            gmId = nextId++;
            items.Add((gmId, "av01", gmx.Data, 1, assocGm));
        }
        int exifId = 0, xmpId = 0;
        if (x?.Exif is { Length: > 0 } exif) { exifId = nextId++; items.Add((exifId, "Exif", ExifItemPayload(exif), 0, null)); }
        if (x?.Xmp is { Length: > 0 } xmp) { xmpId = nextId++; items.Add((xmpId, "mime", xmp, 0, null)); }

        var ipmaBody = new List<byte[]>();
        int assocCount = 0;
        foreach (var it in items)
        {
            if (it.Assoc == null) continue;
            assocCount++;
            ipmaBody.Add(U16(it.Id));
            ipmaBody.Add(new[] { (byte)it.Assoc.Count });
            foreach (var (idx, ess) in it.Assoc) ipmaBody.Add(new[] { (byte)((ess ? 0x80 : 0) | idx) });
        }
        if (props.Count > 127) throw new NotSupportedException("Too many distinct properties for a 7-bit ipma index.");
        byte[] iprp = Box("iprp", Concat(Box("ipco", Concat(props.ToArray())), FullBox("ipma", 0, 0, Concat(U32((uint)assocCount), Concat(ipmaBody.ToArray())))));

        byte[] hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Fourcc("pict"), U32(0), U32(0), U32(0),
            new byte[] { 0 }));   // empty handler name, as libavif
        byte[] pitm = FullBox("pitm", 0, 0, U16(gridId));
        var infes = new List<byte[]> { U16(items.Count) };
        foreach (var it in items)
        {
            byte[] extra = it.Type switch
            {
                "Exif" => System.Text.Encoding.ASCII.GetBytes("Exif\0"),
                "mime" => System.Text.Encoding.ASCII.GetBytes("XMP\0application/rdf+xml\0"),
                _ => new byte[] { 0 },
            };
            infes.Add(FullBox("infe", 2, it.Flags, Concat(U16(it.Id), U16(0), Fourcc(it.Type), extra)));
        }
        byte[] iinf = FullBox("iinf", 0, 0, Concat(infes.ToArray()));

        byte[] Ref(string type, int from, List<int> to)
        {
            var b = new List<byte[]> { U16(from), U16(to.Count) };
            foreach (int t in to) b.Add(U16(t));
            return Box(type, Concat(b.ToArray()));
        }
        var refs = new List<byte[]> { Ref("dimg", gridId, cellIds) };
        if (hasAlpha)
        {
            refs.Add(Ref("dimg", alphaGridId, alphaCellIds));
            refs.Add(Ref("auxl", alphaGridId, [gridId]));
            if (x?.Premultiplied == true) refs.Add(Ref("prem", gridId, [alphaGridId]));
        }
        if (gmx != null) refs.Add(Ref("dimg", tmapId, [gridId, gmId]));
        if (exifId != 0) refs.Add(Ref("cdsc", exifId, [gridId]));
        if (xmpId != 0) refs.Add(Ref("cdsc", xmpId, [gridId]));
        byte[] iref = FullBox("iref", 0, 0, Concat(refs.ToArray()));

        byte[] grpl = gmx != null
            ? Box("grpl", FullBox("altr", 0, 0, Concat(U32((uint)nextId), U32(2), U32((uint)tmapId), U32((uint)gridId))))
            : [];

        var itemOffset = new long[items.Count];
        long pos = 0;
        for (int i = 0; i < items.Count; i++) { itemOffset[i] = pos; pos += items[i].Payload.Length; }
        bool largeOffsets = pos > uint.MaxValue - 1_000_000;
        if (largeOffsets) throw new NotSupportedException("AVIF files over 4 GB are not supported.");
        byte[] Iloc(uint mdatStart)
        {
            var body = new List<byte[]> { new byte[] { 0x44, 0x00 }, U16(items.Count) };
            for (int i = 0; i < items.Count; i++)
                body.Add(IlocEntry(items[i].Id, (uint)(mdatStart + itemOffset[i]), items[i].Payload.Length,
                    layerSizesById.GetValueOrDefault(items[i].Id)));
            return FullBox("iloc", 0, 0, Concat(body.ToArray()));
        }
        byte[] Meta(uint mdatStart) => FullBox("meta", 0, 0, Concat(hdlr, pitm, Iloc(mdatStart), iinf, iref, iprp, grpl));
        int metaLen = Meta(0).Length;
        byte[] meta = Meta((uint)(ftyp.Length + metaLen + 8));
        var payloads = new byte[items.Count][];
        for (int i = 0; i < items.Count; i++) payloads[i] = items[i].Payload;
        return Concat(ftyp, meta, Box("mdat", Concat(payloads)));
    }

    // Sample-entry child boxes (libavif write.c): av1C, then for colour its colr (ICC / nclx) and pasp / clli / mdcv,
    // ccst (coding constraints), and for alpha the auxi aux-track type.
    private static byte[] SampleEntryChildren(byte[] av1C, AvifContainerExtras? x, Av1ObuWriter.Av1ColorDesc? color, bool monochrome,
        bool alpha, bool allKey)
    {
        var parts = new List<byte[]> { av1C };
        if (!alpha)
        {
            if (x?.Icc is { Length: > 0 } icc) parts.Add(Box("colr", Concat(Fourcc("prof"), icc)));
            parts.Add(ColrNclx(color, monochrome));
            if (x?.Pasp is { } pasp) parts.Add(Box("pasp", Concat(U32(pasp.H), U32(pasp.V))));
            if (x?.Clli is { } clli) parts.Add(Box("clli", Concat(U16(clli.MaxCll), U16(clli.MaxPall))));
            if (x?.Mdcv is { Length: 24 } mdcv) parts.Add(Box("mdcv", mdcv));
        }
        // ccst: all_ref_pics_intra(1) intra_pred_used(1) max_ref_per_pic(4) reserved(26). All-key sequences reference
        // nothing; otherwise libavif's permissive (0, 1, 15).
        uint ccst = allKey ? (1u << 31) | (1u << 30) : (1u << 30) | (15u << 26);
        parts.Add(FullBox("ccst", 0, 0, U32(ccst)));
        if (alpha) parts.Add(FullBox("auxi", 0, 0, System.Text.Encoding.ASCII.GetBytes("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha\0")));
        return Concat(parts.ToArray());
    }

    private static byte[] U64(ulong v) => Concat(U32((uint)(v >> 32)), U32((uint)v));

    // moov for an image sequence (libavif write.c): mvhd, then one trak per coded plane set (colour track 1, alpha
    // track 2 with tref auxl -> 1), each with an edit list carrying the repetition count, mdhd/hdlr/minf and a
    // sample table (stsd av01, stts, stsc, stsz, stco; stss only when some sample is not a sync sample).
    private static byte[] Moov(AvifSequenceData sq, int width, int height, byte[] colorEntry, byte[]? alphaEntry,
        uint colorChunkOffset, uint alphaChunkOffset, bool premultiplied)
    {
        byte[] unity = Concat(U32(0x00010000), U32(0), U32(0), U32(0), U32(0x00010000), U32(0), U32(0), U32(0), U32(0x40000000));
        ulong framesDuration = 0;
        foreach (uint d in sq.Durations) framesDuration += d;
        ulong duration = sq.RepetitionCount < 0 ? ulong.MaxValue : framesDuration * (ulong)(sq.RepetitionCount + 1);
        int tracks = alphaEntry != null ? 2 : 1;
        byte[] mvhd = FullBox("mvhd", 1, 0, Concat(U64(sq.CreationTime), U64(sq.ModificationTime), U32(sq.Timescale), U64(duration), U32(0x00010000), U16(0x0100),
            U16(0), new byte[8], unity, new byte[24], U32((uint)(tracks + 1))));

        byte[] Trak(int trackId, List<byte[]> samples, byte[] entryChildren, bool alpha, uint chunkOffset, List<bool>? sync)
        {
            byte[] tkhd = FullBox("tkhd", 1, 1, Concat(U64(sq.CreationTime), U64(sq.ModificationTime), U32((uint)trackId), U32(0), U64(duration), new byte[8],
                U16(0), U16(0), U16(0), U16(0), unity, U32((uint)width << 16), U32((uint)height << 16)));
            var trefParts = new List<byte[]>();
            if (alpha) trefParts.Add(Box("auxl", U32(1)));                         // alpha -> colour
            if (!alpha && premultiplied && sq.AlphaSamples != null) trefParts.Add(Box("prem", U32(2)));   // colour -> alpha
            byte[] tref = trefParts.Count > 0 ? Box("tref", Concat(trefParts.ToArray())) : [];
            byte[] elst = FullBox("elst", 1, sq.RepetitionCount != 0 ? 1u : 0u,
                Concat(U32(1), U64(framesDuration), U64(0), U16(1), U16(0)));
            byte[] edts = Box("edts", elst);
            byte[] mdhd = FullBox("mdhd", 1, 0, Concat(U64(sq.CreationTime), U64(sq.ModificationTime), U32(sq.Timescale), U64(framesDuration), U16(21956), U16(0)));
            byte[] hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Fourcc(alpha ? "auxv" : "pict"), U32(0), U32(0), U32(0), new byte[] { 0 }));
            byte[] vmhd = FullBox("vmhd", 0, 1, Concat(U16(0), new byte[6]));
            byte[] dinf = Box("dinf", FullBox("dref", 0, 0, Concat(U32(1), FullBox("url ", 0, 1, []))));
            byte[] compressor = new byte[32];
            byte[] name = System.Text.Encoding.ASCII.GetBytes("\nAOM Coding");
            name.CopyTo(compressor, 0);
            byte[] av01 = Box("av01", Concat(new byte[6], U16(1), U16(0), U16(0), new byte[12], U16(width), U16(height),
                U32(0x00480000), U32(0x00480000), U32(0), U16(1), compressor, U16(0x0018), U16(0xFFFF), entryChildren));
            byte[] stsd = FullBox("stsd", 0, 0, Concat(U32(1), av01));
            var stts = new List<byte[]>();
            int runs = 0;
            for (int i = 0, count = 0; i < sq.Durations.Length; i++)
            {
                count++;
                if (i + 1 < sq.Durations.Length && sq.Durations[i + 1] == sq.Durations[i]) continue;
                stts.Add(Concat(U32((uint)count), U32(sq.Durations[i])));
                runs++;
                count = 0;
            }
            byte[] sttsBox = FullBox("stts", 0, 0, Concat(U32((uint)runs), Concat(stts.ToArray())));
            byte[] stsc = FullBox("stsc", 0, 0, Concat(U32(1), U32(1), U32((uint)samples.Count), U32(1)));
            var sizes = new List<byte[]> { U32(0), U32((uint)samples.Count) };
            foreach (var smp in samples) sizes.Add(U32((uint)smp.Length));
            byte[] stsz = FullBox("stsz", 0, 0, Concat(sizes.ToArray()));
            byte[] stco = FullBox("stco", 0, 0, Concat(U32(1), U32(chunkOffset)));
            // stss (ISO/IEC 14496-12 8.6.2): absent = every sample is a sync sample.
            byte[] stss = [];
            if (sync != null && !sync.TrueForAll(v => v))
            {
                var entries = new List<byte[]> { U32((uint)sync.Count(v => v)) };
                for (int i = 0; i < sync.Count; i++) if (sync[i]) entries.Add(U32((uint)(i + 1)));
                stss = FullBox("stss", 0, 0, Concat(entries.ToArray()));
            }
            byte[] stbl = Box("stbl", Concat(stsd, sttsBox, stsc, stsz, stco, stss));
            byte[] minf = Box("minf", Concat(vmhd, dinf, stbl));
            byte[] mdia = Box("mdia", Concat(mdhd, hdlr, minf));
            return Box("trak", Concat(tkhd, tref, edts, mdia));
        }

        var body = new List<byte[]> { mvhd, Trak(1, sq.ColorSamples, colorEntry, false, colorChunkOffset, sq.ColorSync) };
        if (alphaEntry != null) body.Add(Trak(2, sq.AlphaSamples!, alphaEntry, true, alphaChunkOffset, sq.AlphaSync));
        return Box("moov", Concat(body.ToArray()));
    }

    // HEIF Exif item: unsigned int(32) exif_tiff_header_offset, then the Exif block. The offset is the distance from
    // the block start to the TIFF header ("II*\0" / "MM\0*"), e.g. 6 when the block starts with "Exif\0\0".
    private static byte[] ExifItemPayload(byte[] exif)
    {
        int off = 0;
        for (int i = 0; i + 4 <= exif.Length; i++)
            if ((exif[i] == 'I' && exif[i + 1] == 'I' && exif[i + 2] == 42 && exif[i + 3] == 0) ||
                (exif[i] == 'M' && exif[i + 1] == 'M' && exif[i + 2] == 0 && exif[i + 3] == 42)) { off = i; break; }
        return Concat(U32((uint)off), exif);
    }

    internal static byte[] Box(string type, byte[] payload) => Concat(U32((uint)(payload.Length + 8)), Fourcc(type), payload);

    internal static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
        => Box(type, Concat(new byte[] { version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags }, payload));

    private static byte[] Fourcc(string s) => new[] { (byte)s[0], (byte)s[1], (byte)s[2], (byte)s[3] };

    private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };

    private static void WriteU32(byte[] buf, int pos, uint v)
    {
        buf[pos] = (byte)(v >> 24);
        buf[pos + 1] = (byte)(v >> 16);
        buf[pos + 2] = (byte)(v >> 8);
        buf[pos + 3] = (byte)v;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int len = 0;
        foreach (byte[] p in parts)
        {
            len += p.Length;
        }

        var outb = new byte[len];
        int o = 0;
        foreach (byte[] p in parts)
        {
            Buffer.BlockCopy(p, 0, outb, o, p.Length);
            o += p.Length;
        }

        return outb;
    }
}
