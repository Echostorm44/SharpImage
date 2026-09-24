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
}
