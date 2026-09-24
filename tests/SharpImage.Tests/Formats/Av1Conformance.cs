using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Conformance helpers: demux IVF / Matroska AV1 streams into temporal units, decode them with Av1Decoder and hash
// every output frame the way libaom's test_vector_test does (test/md5_helper.h: each plane's visible rows, 8-bit
// samples as bytes, deeper ones as 16-bit little-endian; chroma sizes rounded up).
internal static class Av1Conformance
{
    internal static List<byte[]> ReadIvf(byte[] d)
    {
        var tus = new List<byte[]>();
        if (d.Length < 32 || d[0] != 'D' || d[1] != 'K' || d[2] != 'I' || d[3] != 'F') throw new InvalidDataException("not IVF");
        int hdr = d[6] | (d[7] << 8);
        for (int p = hdr; p + 12 <= d.Length;)
        {
            int size = d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24);
            p += 12;
            tus.Add(d.AsSpan(p, size).ToArray());
            p += size;
        }
        return tus;
    }

    // Minimal Matroska: every SimpleBlock / Block payload (no lacing) of the first video track, in file order.
    internal static List<byte[]> ReadMkv(byte[] d)
    {
        var tus = new List<byte[]>();
        Walk(0, d.Length);
        return tus;

        void Walk(int p, int end)
        {
            while (p < end)
            {
                long id = ReadVint(d, ref p, keepMarker: true);
                int sp = p;
                long size = ReadVint(d, ref p, keepMarker: false);
                bool unknown = size == (1L << (7 * (p - sp))) - 1;
                int bodyEnd = unknown ? end : (int)Math.Min(end, p + size);
                switch (id)
                {
                    case 0x18538067: case 0x1F43B675: case 0xA0:   // Segment, Cluster, BlockGroup
                        Walk(p, bodyEnd);
                        break;
                    case 0xA3: case 0xA1:                          // SimpleBlock, Block
                    {
                        int q = p;
                        ReadVint(d, ref q, keepMarker: false);     // track number
                        q += 3;                                    // timecode (2) + flags (1)
                        tus.Add(d.AsSpan(q, bodyEnd - q).ToArray());
                        break;
                    }
                }
                p = bodyEnd;
            }
        }
    }

    private static long ReadVint(byte[] d, ref int p, bool keepMarker)
    {
        int first = d[p];
        int len = 1;
        while (len <= 8 && (first & (0x80 >> (len - 1))) == 0) len++;
        long v = keepMarker ? first : first & ((0x80 >> (len - 1)) - 1);
        for (int i = 1; i < len; i++) v = (v << 8) | d[p + i];
        p += len;
        return v;
    }

    internal static string FrameMd5(DecodedVideoFrame f, bool monochrome)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        int ssx = f.Format is PixelFormat.Yuv444P or PixelFormat.Yuv444P10 or PixelFormat.Yuv444P12 ? 0 : 1;
        int ssy = f.Format is PixelFormat.Yuv420P or PixelFormat.Yuv420P10 or PixelFormat.Yuv420P12 ? 1 : 0;
        bool hbd = f.BitDepth > 8;
        for (int pl = 0; pl < 3; pl++)
        {
            int w = pl == 0 ? f.Width : (f.Width + ssx) >> ssx, h = pl == 0 ? f.Height : (f.Height + ssy) >> ssy;
            int stride = pl == 0 ? f.YStride : pl == 1 ? f.UStride : f.VStride;
            var row = new byte[w * (hbd ? 2 : 1)];
            for (int y = 0; y < h; y++)
            {
                if (monochrome && pl > 0)
                {
                    int mid = 1 << (f.BitDepth - 1);
                    for (int x = 0; x < w; x++)
                        if (hbd) { row[2 * x] = (byte)mid; row[2 * x + 1] = (byte)(mid >> 8); } else row[x] = (byte)mid;
                }
                else if (hbd)
                {
                    var src = (pl == 0 ? f.YPlane16 : pl == 1 ? f.UPlane16 : f.VPlane16).Span.Slice(y * stride, w);
                    for (int x = 0; x < w; x++) { row[2 * x] = (byte)src[x]; row[2 * x + 1] = (byte)(src[x] >> 8); }
                }
                else (pl == 0 ? f.YPlane : pl == 1 ? f.UPlane : f.VPlane).Span.Slice(y * stride, w).CopyTo(row);
                md5.AppendData(row);
            }
        }
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>Decodes one test vector and compares every output frame with its .md5 file. Returns "ok N" or the
    /// first mismatch / error.</summary>
    internal static string CheckVector(string path)
    {
        var expected = new List<string>();
        foreach (var line in File.ReadAllLines(path + ".md5"))
            if (line.Length >= 32) expected.Add(line[..32]);
        byte[] d = File.ReadAllBytes(path);
        var tus = path.EndsWith(".mkv") || path.EndsWith(".webm") ? ReadMkv(d) : ReadIvf(d);
        var dec = new Av1Decoder();
        int n = 0;
        try
        {
            for (int i = 0; i < tus.Count; i++)
            {
                Av1Decoder.LastDecodeError = null;
                using var f = dec.Decode(tus[i], i, i == 0);
                if (f == null)
                {
                    if (Av1Decoder.LastDecodeError is { } err) return $"ERROR tu {i}: {err.Split('\n')[0]}";
                    continue;
                }
                if (n >= expected.Count) return $"EXTRA frame {n} (expected {expected.Count})";
                string got = FrameMd5(f, dec.Monochrome);
                if (got != expected[n]) return $"MISMATCH frame {n} of {expected.Count} (tu {i}, {f.Width}x{f.Height} bd{f.BitDepth} {f.Format})";
                n++;
            }
        }
        catch (Exception e) { return $"EXCEPTION frame {n}: {e.GetType().Name} {e.Message.Split('\n')[0]}"; }
        return n == expected.Count ? $"ok {n}" : $"SHORT {n} of {expected.Count}";
    }

    // ── Argon (AOM conformance suite, as dav1d's tests/dav1d_argon.bash runs it) ──────────────────────────────────

    // A raw .obu file as temporal units of Section 5 OBUs (every OBU given a size field). Annex B streams are
    // length-prefixed temporal units / frame units / OBUs; Section 5 streams are split at temporal delimiters.
    internal static List<byte[]> ReadObuFile(byte[] d, bool annexB)
    {
        var tus = new List<byte[]>();
        if (annexB)
        {
            for (int p = 0; p < d.Length;)
            {
                long tuSize = Leb(d, ref p);
                int tuEnd = (int)Math.Min(d.Length, p + tuSize);
                var tu = new List<byte>();
                while (p < tuEnd)
                {
                    long fuSize = Leb(d, ref p);
                    int fuEnd = (int)Math.Min(tuEnd, p + fuSize);
                    while (p < fuEnd)
                    {
                        long obuLen = Leb(d, ref p);
                        int len = (int)Math.Min(fuEnd - p, obuLen);
                        if (len > 0) AppendWithSize(tu, d.AsSpan(p, len));
                        p += len;
                    }
                    p = fuEnd;
                }
                tus.Add([.. tu]);
                p = tuEnd;
            }
            return tus;
        }
        var cur = new List<byte>();
        for (int p = 0; p < d.Length;)
        {
            int start = p, hb = d[p], ext = (hb >> 2) & 1;
            int q = p + 1 + ext;
            long size = (hb & 2) != 0 ? Leb(d, ref q) : d.Length - q;
            int end = (int)Math.Min(d.Length, q + size);
            if (((hb >> 3) & 15) == 2 && cur.Count > 0) { tus.Add([.. cur]); cur.Clear(); }
            cur.AddRange(d.AsSpan(start, end - start).ToArray());
            p = end;
        }
        if (cur.Count > 0) tus.Add([.. cur]);
        return tus;
    }

    private static void AppendWithSize(List<byte> o, ReadOnlySpan<byte> obu)
    {
        int hb = obu[0], ext = (hb >> 2) & 1, hdr = 1 + ext;
        if ((hb & 2) != 0) { o.AddRange(obu.ToArray()); return; }
        o.Add((byte)(hb | 2));
        if (ext != 0) o.Add(obu[1]);
        uint len = (uint)(obu.Length - hdr);
        do { byte b = (byte)(len & 0x7F); len >>= 7; if (len != 0) b |= 0x80; o.Add(b); } while (len != 0);
        o.AddRange(obu[hdr..].ToArray());
    }

    private static long Leb(byte[] d, ref int p)
    {
        long v = 0;
        for (int i = 0; i < 8 && p < d.Length; i++)
        {
            byte b = d[p++];
            v |= (long)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) break;
        }
        return v;
    }

    // dav1d's md5 muxer (tools/output/md5.c) over every output frame: visible rows of each plane, 16-bit samples as
    // little-endian, chroma omitted for 4:0:0.
    internal static void AddDav1dMd5(IncrementalHash md5, DecodedVideoFrame f, bool monochrome)
    {
        int ssx = f.Format is PixelFormat.Yuv444P or PixelFormat.Yuv444P10 or PixelFormat.Yuv444P12 ? 0 : 1;
        int ssy = f.Format is PixelFormat.Yuv420P or PixelFormat.Yuv420P10 or PixelFormat.Yuv420P12 ? 1 : 0;
        bool hbd = f.BitDepth > 8;
        for (int pl = 0; pl < (monochrome ? 1 : 3); pl++)
        {
            int w = pl == 0 ? f.Width : (f.Width + ssx) >> ssx, h = pl == 0 ? f.Height : (f.Height + ssy) >> ssy;
            int stride = pl == 0 ? f.YStride : pl == 1 ? f.UStride : f.VStride;
            var row = new byte[w * (hbd ? 2 : 1)];
            for (int y = 0; y < h; y++)
            {
                if (hbd)
                {
                    var src = (pl == 0 ? f.YPlane16 : pl == 1 ? f.UPlane16 : f.VPlane16).Span.Slice(y * stride, w);
                    for (int x = 0; x < w; x++) { row[2 * x] = (byte)src[x]; row[2 * x + 1] = (byte)(src[x] >> 8); }
                }
                else (pl == 0 ? f.YPlane : pl == 1 ? f.UPlane : f.VPlane).Span.Slice(y * stride, w).CopyTo(row);
                md5.AppendData(row);
            }
        }
    }

    /// <summary>One Argon stream against its md5_ref (film grain applied, operating point 0, all layers output).</summary>
    internal static string CheckArgon(string obuPath, string md5Path, bool annexB)
    {
        string expected = File.ReadAllText(md5Path).Trim().Split(' ', '\t', '\n', '\r')[0];
        var tus = ReadObuFile(File.ReadAllBytes(obuPath), annexB);
        var dec = new Av1Decoder();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        int frames = 0;
        try
        {
            for (int i = 0; i < tus.Count; i++)
            {
                Av1Decoder.LastDecodeError = null;
                var outs = dec.DecodeTemporalUnit(tus[i], i);
                if (outs.Count == 0 && Av1Decoder.LastDecodeError is { } err) return $"ERROR tu {i}: {err.Split('\n')[0]}";
                foreach (var (f, _) in outs)
                {
                    AddDav1dMd5(md5, f, dec.Monochrome);
                    f.Dispose();
                    frames++;
                }
            }
        }
        catch (Exception e) { return $"EXCEPTION after {frames} frames: {e.GetType().Name} {e.Message.Split('\n')[0]}"; }
        string got = Convert.ToHexStringLower(md5.GetHashAndReset());
        return got == expected ? $"ok {frames}" : $"MISMATCH ({frames} frames)";
    }

    /// <summary>Triage: every output frame of an Argon stream written as dav1d -o out.yuv would (planes back to back,
    /// 16-bit LE above 8 bits, luma only for 4:0:0), plus one "w h bitdepth layout mono" line per frame.</summary>
    internal static void DumpArgon(string obuPath, bool annexB, string yuvOut, string infoOut)
    {
        var tus = ReadObuFile(File.ReadAllBytes(obuPath), annexB);
        var dec = new Av1Decoder();
        using var y = File.Create(yuvOut);
        using var info = new StreamWriter(infoOut);
        for (int i = 0; i < tus.Count; i++)
        {
            Av1Decoder.LastDecodeError = null;
            List<(DecodedVideoFrame Frame, int SpatialId)> outs;
            try { outs = dec.DecodeTemporalUnit(tus[i], i); }
            catch (Exception e) { info.WriteLine($"EXCEPTION tu {i} {e.GetType().Name} {e.Message.Split('\n')[0]}"); return; }
            if (outs.Count == 0 && Av1Decoder.LastDecodeError is { } err) { info.WriteLine($"ERROR tu {i} {err.Split('\n')[0]}"); return; }
            foreach (var (f, sid) in outs)
            {
                WriteRaw(y, f, dec.Monochrome);
                info.WriteLine($"{f.Width} {f.Height} {f.BitDepth} {f.Format} {(dec.Monochrome ? 1 : 0)} tu{i} sid{sid}");
                f.Dispose();
            }
        }
    }

    private static void WriteRaw(Stream o, DecodedVideoFrame f, bool monochrome)
    {
        int ssx = f.Format is PixelFormat.Yuv444P or PixelFormat.Yuv444P10 or PixelFormat.Yuv444P12 ? 0 : 1;
        int ssy = f.Format is PixelFormat.Yuv420P or PixelFormat.Yuv420P10 or PixelFormat.Yuv420P12 ? 1 : 0;
        bool hbd = f.BitDepth > 8;
        for (int pl = 0; pl < (monochrome ? 1 : 3); pl++)
        {
            int w = pl == 0 ? f.Width : (f.Width + ssx) >> ssx, h = pl == 0 ? f.Height : (f.Height + ssy) >> ssy;
            int stride = pl == 0 ? f.YStride : pl == 1 ? f.UStride : f.VStride;
            var row = new byte[w * (hbd ? 2 : 1)];
            for (int yy = 0; yy < h; yy++)
            {
                if (hbd)
                {
                    var src = (pl == 0 ? f.YPlane16 : pl == 1 ? f.UPlane16 : f.VPlane16).Span.Slice(yy * stride, w);
                    for (int x = 0; x < w; x++) { row[2 * x] = (byte)src[x]; row[2 * x + 1] = (byte)(src[x] >> 8); }
                }
                else (pl == 0 ? f.YPlane : pl == 1 ? f.UPlane : f.VPlane).Span.Slice(yy * stride, w).CopyTo(row);
                o.Write(row);
            }
        }
    }
}
