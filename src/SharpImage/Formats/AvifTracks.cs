using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace SharpImage.Formats;

/// <summary>One track of an AVIF image sequence ('avis'): identity, timing and the sample table (after libavif).</summary>
internal sealed class AvifTrack
{
    public uint Id;
    public string Handler = "";
    public int Width, Height;              // tkhd (16.16 fixed point, integer part)
    public ulong TrackDuration;            // tkhd, in the movie timescale (all ones = indefinite)
    public uint MediaTimescale;            // mdhd
    public ulong MediaDuration;            // mdhd
    public bool HasEdts, IsRepeating;      // edts/elst flags & 1
    public ulong SegmentDuration;          // elst (single entry)
    public uint AuxForId, PremById;        // tref auxl / prem
    public string Codec = "";              // sample entry 4CC ('av01')
    public readonly List<(string Type, int Off, int Len)> Properties = [];   // sample-entry child boxes (av1C, colr, ...)
    public readonly List<(long Offset, int Size)> Samples = [];
    public uint[] Durations = [];          // per sample, media timescale units
    public bool[] Sync = [];               // stss (absent = every sample is a sync sample)

    /// <summary>libavif's repetition count: -1 infinite, -2 unknown (no edit list), else extra repetitions.</summary>
    public int RepetitionCount
    {
        get
        {
            if (!HasEdts) return -2;
            if (!IsRepeating) return 0;
            if (TrackDuration == ulong.MaxValue || TrackDuration == uint.MaxValue) return -1;
            if (SegmentDuration == 0 || TrackDuration == 0) return 0;
            ulong r = TrackDuration / SegmentDuration + (TrackDuration % SegmentDuration != 0 ? 1UL : 0UL) - 1;
            return r > int.MaxValue ? -1 : (int)r;
        }
    }

    public (int Off, int Len)? Property(string type)
    {
        foreach (var p in Properties) if (p.Type == type) return (p.Off, p.Len);
        return null;
    }
}

/// <summary>ISO BMFF 'moov' parsing for AVIF image sequences (tracks, sample tables, edit lists).</summary>
internal static class AvifTracks
{
    public static (string MajorBrand, List<AvifTrack> Tracks) Parse(byte[] d)
    {
        string major = "";
        var tracks = new List<AvifTrack>();
        foreach (var (type, off, len) in HeifContainer.Children(d, 0, d.Length))
        {
            if (type == "ftyp" && len >= 4) major = System.Text.Encoding.ASCII.GetString(d, off, 4);
            else if (type == "moov")
            {
                foreach (var (t2, o2, l2) in HeifContainer.Children(d, off, len))
                    if (t2 == "trak") tracks.Add(ParseTrak(d, o2, l2));
            }
        }
        return (major, tracks);
    }

    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
    private static ulong U64(byte[] d, int o) => BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(o));

    private static AvifTrack ParseTrak(byte[] d, int off, int len)
    {
        var t = new AvifTrack();
        foreach (var (type, o, l) in HeifContainer.Children(d, off, len))
        {
            switch (type)
            {
                case "tkhd":
                {
                    int v = d[o];
                    t.Id = v == 1 ? U32(d, o + 20) : U32(d, o + 12);
                    t.TrackDuration = v == 1 ? U64(d, o + 28) : U32(d, o + 20);
                    if (v != 1 && t.TrackDuration == uint.MaxValue) t.TrackDuration = ulong.MaxValue;
                    t.Width = (int)(U32(d, o + l - 8) >> 16);
                    t.Height = (int)(U32(d, o + l - 4) >> 16);
                    break;
                }
                case "tref":
                    foreach (var (rt, ro, rl) in HeifContainer.Children(d, o, l))
                    {
                        if (rl < 4) continue;
                        if (rt == "auxl") t.AuxForId = U32(d, ro);
                        else if (rt == "prem") t.PremById = U32(d, ro);
                    }
                    break;
                case "edts":
                    t.HasEdts = true;
                    foreach (var (et, eo, el) in HeifContainer.Children(d, o, l))
                    {
                        if (et != "elst") continue;
                        int v = d[eo];
                        int flags = (d[eo + 1] << 16) | (d[eo + 2] << 8) | d[eo + 3];
                        t.IsRepeating = (flags & 1) != 0;
                        if (!t.IsRepeating) continue;
                        uint count = U32(d, eo + 4);
                        if (count != 1) throw new InvalidDataException("AVIF elst must have exactly one entry.");
                        t.SegmentDuration = v == 1 ? U64(d, eo + 8) : U32(d, eo + 8);
                        if (t.SegmentDuration == 0) throw new InvalidDataException("AVIF elst segment_duration is 0.");
                    }
                    break;
                case "mdia":
                    ParseMdia(d, o, l, t);
                    break;
            }
        }
        return t;
    }

    private static void ParseMdia(byte[] d, int off, int len, AvifTrack t)
    {
        foreach (var (type, o, l) in HeifContainer.Children(d, off, len))
        {
            if (type == "mdhd")
            {
                int v = d[o];
                t.MediaTimescale = v == 1 ? U32(d, o + 20) : U32(d, o + 12);
                t.MediaDuration = v == 1 ? U64(d, o + 24) : U32(d, o + 16);
            }
            else if (type == "hdlr" && l >= 12) t.Handler = System.Text.Encoding.ASCII.GetString(d, o + 8, 4);
            else if (type == "minf")
                foreach (var (mt, mo, ml) in HeifContainer.Children(d, o, l))
                    if (mt == "stbl") ParseStbl(d, mo, ml, t);
        }
    }

    private static void ParseStbl(byte[] d, int off, int len, AvifTrack t)
    {
        var chunkOffsets = new List<long>();
        var stsc = new List<(uint FirstChunk, uint PerChunk)>();
        var sizes = new List<int>();
        var durations = new List<uint>();
        List<uint>? sync = null;
        foreach (var (type, o, l) in HeifContainer.Children(d, off, len))
        {
            switch (type)
            {
                case "stsd":
                {
                    uint count = U32(d, o + 4);
                    if (count == 0) break;
                    int eo = o + 8;
                    int esz = (int)U32(d, eo);
                    t.Codec = System.Text.Encoding.ASCII.GetString(d, eo + 4, 4);
                    // VisualSampleEntry: 8-byte box header + 78 bytes of fixed fields, then child boxes.
                    if (esz > 86) t.Properties.AddRange(HeifContainer.Children(d, eo + 86, esz - 86));
                    break;
                }
                case "stts":
                {
                    uint n = U32(d, o + 4);
                    for (int i = 0; i < n; i++)
                    {
                        uint cnt = U32(d, o + 8 + 8 * i), delta = U32(d, o + 12 + 8 * i);
                        for (uint k = 0; k < cnt; k++) durations.Add(delta);
                    }
                    break;
                }
                case "stss":
                {
                    uint n = U32(d, o + 4);
                    sync = [];
                    for (int i = 0; i < n; i++) sync.Add(U32(d, o + 8 + 4 * i));
                    break;
                }
                case "stsc":
                {
                    uint n = U32(d, o + 4);
                    for (int i = 0; i < n; i++) stsc.Add((U32(d, o + 8 + 12 * i), U32(d, o + 12 + 12 * i)));
                    break;
                }
                case "stsz":
                {
                    uint fixedSize = U32(d, o + 4), n = U32(d, o + 8);
                    for (int i = 0; i < n; i++) sizes.Add((int)(fixedSize != 0 ? fixedSize : U32(d, o + 12 + 4 * i)));
                    break;
                }
                case "stz2":
                {
                    int field = d[o + 7];
                    uint n = U32(d, o + 8);
                    for (int i = 0; i < n; i++)
                        sizes.Add(field switch
                        {
                            4 => (d[o + 12 + i / 2] >> (i % 2 == 0 ? 4 : 0)) & 15,
                            8 => d[o + 12 + i],
                            _ => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o + 12 + 2 * i)),
                        });
                    break;
                }
                case "stco":
                {
                    uint n = U32(d, o + 4);
                    for (int i = 0; i < n; i++) chunkOffsets.Add(U32(d, o + 8 + 4 * i));
                    break;
                }
                case "co64":
                {
                    uint n = U32(d, o + 4);
                    for (int i = 0; i < n; i++) chunkOffsets.Add((long)U64(d, o + 8 + 8 * i));
                    break;
                }
            }
        }

        // Sample positions: walk the chunks, taking samples_per_chunk from the stsc run covering each chunk.
        int sample = 0;
        for (int c = 0; c < chunkOffsets.Count && sample < sizes.Count; c++)
        {
            uint perChunk = 0;
            foreach (var (first, per) in stsc) if (first <= c + 1) perChunk = per;
            long pos = chunkOffsets[c];
            for (uint k = 0; k < perChunk && sample < sizes.Count; k++)
            {
                t.Samples.Add((pos, sizes[sample]));
                pos += sizes[sample];
                sample++;
            }
        }
        t.Durations = new uint[t.Samples.Count];
        for (int i = 0; i < t.Durations.Length; i++) t.Durations[i] = i < durations.Count ? durations[i] : 1;
        t.Sync = new bool[t.Samples.Count];
        for (int i = 0; i < t.Sync.Length; i++) t.Sync[i] = sync == null || sync.Contains((uint)(i + 1));
    }
}
