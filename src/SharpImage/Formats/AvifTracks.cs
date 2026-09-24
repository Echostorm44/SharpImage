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
    public ulong CreationTime, ModificationTime;   // mdhd, seconds since 1904-01-01 UTC
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

/// <summary>ISO BMFF 'moov' parsing for AVIF image sequences (tracks, sample tables, edit lists). Every field read is
/// bounded by its box, as libavif's avifROStream: a truncated box fails the parse (InvalidDataException).</summary>
internal static class AvifTracks
{
    /// <summary>Tracks of the file; at most <paramref name="maxSamples"/> samples per track (libavif imageCountLimit;
    /// 0: no limit), each lying inside the file.</summary>
    public static (string MajorBrand, List<AvifTrack> Tracks) Parse(byte[] d, int maxSamples = 0)
    {
        string major = "";
        var tracks = new List<AvifTrack>();
        foreach (var (type, off, len) in HeifContainer.Children(d, 0, d.Length))
        {
            if (type == "ftyp" && len >= 4) major = System.Text.Encoding.ASCII.GetString(d, off, 4);
            else if (type == "moov")
            {
                foreach (var (t2, o2, l2) in HeifContainer.Children(d, off, len))
                    if (t2 == "trak") tracks.Add(ParseTrak(d, o2, l2, maxSamples));
            }
        }
        return (major, tracks);
    }

    private static AvifTrack ParseTrak(byte[] d, int off, int len, int maxSamples)
    {
        var t = new AvifTrack();
        foreach (var (type, o, l) in HeifContainer.Children(d, off, len))
        {
            switch (type)
            {
                case "tkhd":
                {
                    var b = new BoxReader(d, o, l, "tkhd");
                    var (v, _) = b.FullBox();
                    b.Skip(v == 1 ? 16 : 8);                                   // creation / modification time
                    t.Id = b.U32();
                    b.Skip(4);                                                  // reserved
                    t.TrackDuration = v == 1 ? b.U64() : b.U32();
                    if (v != 1 && t.TrackDuration == uint.MaxValue) t.TrackDuration = ulong.MaxValue;
                    b.Skip(8 + 2 + 2 + 2 + 2 + 36);                             // reserved, layer, group, volume, reserved, matrix
                    t.Width = (int)(b.U32() >> 16);
                    t.Height = (int)(b.U32() >> 16);
                    break;
                }
                case "tref":
                    foreach (var (rt, ro, rl) in HeifContainer.Children(d, o, l))
                    {
                        if (rl < 4) continue;
                        var b = new BoxReader(d, ro, rl, rt);
                        if (rt == "auxl") t.AuxForId = b.U32();
                        else if (rt == "prem") t.PremById = b.U32();
                    }
                    break;
                case "edts":
                    t.HasEdts = true;
                    foreach (var (et, eo, el) in HeifContainer.Children(d, o, l))
                    {
                        if (et != "elst") continue;
                        var b = new BoxReader(d, eo, el, "elst");
                        var (v, flags) = b.FullBox();
                        t.IsRepeating = (flags & 1) != 0;
                        if (!t.IsRepeating) continue;
                        uint count = b.U32();
                        if (count != 1) throw new InvalidDataException("AVIF elst must have exactly one entry.");
                        t.SegmentDuration = v == 1 ? b.U64() : b.U32();
                        if (t.SegmentDuration == 0) throw new InvalidDataException("AVIF elst segment_duration is 0.");
                    }
                    break;
                case "mdia":
                    ParseMdia(d, o, l, t, maxSamples);
                    break;
            }
        }
        return t;
    }

    private static void ParseMdia(byte[] d, int off, int len, AvifTrack t, int maxSamples)
    {
        foreach (var (type, o, l) in HeifContainer.Children(d, off, len))
        {
            if (type == "mdhd")
            {
                var b = new BoxReader(d, o, l, "mdhd");
                var (v, _) = b.FullBox();
                t.CreationTime = v == 1 ? b.U64() : b.U32();
                t.ModificationTime = v == 1 ? b.U64() : b.U32();
                t.MediaTimescale = b.U32();
                t.MediaDuration = v == 1 ? b.U64() : b.U32();
            }
            else if (type == "hdlr" && l >= 12) t.Handler = System.Text.Encoding.ASCII.GetString(d, o + 8, 4);
            else if (type == "minf")
                foreach (var (mt, mo, ml) in HeifContainer.Children(d, o, l))
                    if (mt == "stbl") ParseStbl(d, mo, ml, t, maxSamples);
        }
    }

    private static void ParseStbl(byte[] d, int off, int len, AvifTrack t, int maxSamples)
    {
        long cap = maxSamples > 0 ? maxSamples : int.MaxValue;
        var chunkOffsets = new List<long>();
        var stsc = new List<(uint FirstChunk, uint PerChunk)>();
        var sizes = new List<int>();
        uint fixedSize = 0;
        long fixedCount = -1;
        var timeRuns = new List<(uint Count, uint Delta)>();
        List<uint>? sync = null;
        foreach (var (type, o, l) in HeifContainer.Children(d, off, len))
        {
            var b = new BoxReader(d, o, l, type);
            switch (type)
            {
                case "stsd":
                {
                    b.FullBox();
                    uint count = b.U32();
                    if (count == 0) break;
                    int eo = b.Pos;
                    long esz = b.U32();
                    if (esz < 8 || esz > l - (eo - o)) throw new InvalidDataException("Truncated Box[stsd].");
                    t.Codec = System.Text.Encoding.ASCII.GetString(d, eo + 4, 4);
                    // VisualSampleEntry: 8-byte box header + 78 bytes of fixed fields, then child boxes.
                    if (esz > 86) t.Properties.AddRange(HeifContainer.Children(d, eo + 86, (int)esz - 86));
                    break;
                }
                case "stts":
                {
                    b.FullBox();
                    uint n = b.U32();
                    for (uint i = 0; i < n; i++) timeRuns.Add((b.U32(), b.U32()));
                    break;
                }
                case "stss":
                {
                    b.FullBox();
                    uint n = b.U32();
                    sync = [];
                    for (uint i = 0; i < n; i++) sync.Add(b.U32());
                    break;
                }
                case "stsc":
                {
                    b.FullBox();
                    uint n = b.U32();
                    for (uint i = 0; i < n; i++)
                    {
                        uint first = b.U32(), per = b.U32();
                        b.U32();                                               // sample_description_index
                        stsc.Add((first, per));
                    }
                    break;
                }
                case "stsz":
                {
                    b.FullBox();
                    fixedSize = b.U32();
                    uint n = b.U32();
                    if (n > cap) throw new InvalidDataException("Exceeded the image count limit.");
                    if (fixedSize != 0) fixedCount = n;
                    else for (uint i = 0; i < n; i++) sizes.Add((int)Math.Min(b.U32(), int.MaxValue));
                    break;
                }
                case "stz2":
                {
                    b.FullBox();
                    b.Skip(3);
                    int field = b.U8();
                    uint n = b.U32();
                    if (n > cap) throw new InvalidDataException("Exceeded the image count limit.");
                    if (field is not (4 or 8 or 16)) throw new InvalidDataException("Invalid Box[stz2] field size.");
                    for (uint i = 0; i < n; i++)
                    {
                        if (field == 4) { byte v = b.U8(); sizes.Add(v >> 4); if (++i < n) sizes.Add(v & 15); }
                        else sizes.Add(field == 8 ? b.U8() : b.U16());
                    }
                    break;
                }
                case "stco":
                {
                    b.FullBox();
                    uint n = b.U32();
                    for (uint i = 0; i < n; i++) chunkOffsets.Add(b.U32());
                    break;
                }
                case "co64":
                {
                    b.FullBox();
                    uint n = b.U32();
                    for (uint i = 0; i < n; i++) chunkOffsets.Add((long)Math.Min(b.U64(), long.MaxValue));
                    break;
                }
            }
        }

        // Sample positions: walk the chunks, taking samples_per_chunk from the stsc run covering each chunk.
        long sampleCount = fixedCount >= 0 ? fixedCount : sizes.Count;
        int SizeOf(int i) => fixedCount >= 0 ? (int)Math.Min(fixedSize, int.MaxValue) : sizes[i];
        int sample = 0;
        for (int c = 0; c < chunkOffsets.Count && sample < sampleCount; c++)
        {
            uint perChunk = 0;
            foreach (var (first, per) in stsc) if (first <= c + 1) perChunk = per;
            if (perChunk == 0) throw new InvalidDataException("Sample table contains a chunk with 0 samples.");
            long pos = chunkOffsets[c];
            for (uint k = 0; k < perChunk && sample < sampleCount; k++)
            {
                int size = SizeOf(sample);
                if (pos < 0 || pos > d.Length - (long)size) throw new InvalidDataException($"Sample {sample} lies outside the file.");
                t.Samples.Add((pos, size));
                pos += size;
                sample++;
            }
        }
        t.Durations = new uint[t.Samples.Count];
        int di = 0;
        foreach (var (count, delta) in timeRuns)
            for (uint k = 0; k < count && di < t.Durations.Length; k++) t.Durations[di++] = delta;
        for (; di < t.Durations.Length; di++) t.Durations[di] = 1;
        t.Sync = new bool[t.Samples.Count];
        if (sync == null) Array.Fill(t.Sync, true);
        else foreach (uint s1 in sync) if (s1 >= 1 && s1 <= t.Sync.Length) t.Sync[s1 - 1] = true;
    }
}
