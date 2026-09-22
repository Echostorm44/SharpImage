using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using SharpImage.Formats;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Decoder conformance against real third-party AVIFs (TestAssets/avif_conformance): current libavif (libaom) defaults
// use quantizer matrices + per-superblock delta-q, and libaom's delta-lf-mode adds per-superblock loop-filter deltas.
// Each stream's native-depth YUV planes must hash identically to ffmpeg/libdav1d's decode (golden MD5s computed
// with `ffmpeg -pix_fmt <native>`); the alpha asset's alpha plane must match libavif's decode exactly.
public sealed class Av1ConformanceDecodeTests
{
    private static string Asset(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name);

    [Test]
    [Arguments("libavif_8bit_444_qm_dq.avif", 384, 256, "2d05d5dc8e76405a2da4a71829ec84de")]
    [Arguments("libavif_10bit_420_qm_dq.avif", 384, 256, "e067e8a640ab96cb615d7400964bce49")]
    [Arguments("libavif_10bit_422_qm_dq.avif", 384, 256, "e0d43bdb2886357183ff1f678a3bfb06")]
    [Arguments("libavif_12bit_444_qm_dq.avif", 384, 256, "e6ce1414e1898f4c3ed2be6684a6f94d")]
    [Arguments("libaom_10bit_420_qm_dq_dlf.avif", 384, 256, "1f22d8cb02a9555a91d74b93fd269e64")]
    [Arguments("libavif_10bit_420_alpha.avif", 160, 96, "b70e1446b1e8656c5605b74ef678785b")]
    public async Task DecodesByteExactVsDav1d(string file, int w, int h, string md5)
    {
        byte[] item = PrimaryItemData(File.ReadAllBytes(Asset(file)));
        var dec = new Av1Decoder();
        using var yuv = dec.Decode(item, 0, isKeyframe: true);
        await Assert.That(yuv).IsNotNull();
        await Assert.That(yuv!.Width).IsEqualTo(w);
        await Assert.That(Hex(MD5.HashData(NativePlanes(yuv)))).IsEqualTo(md5);
    }

    [Test]
    public async Task AlphaMatchesLibavif()
    {
        var img = HeifCoder.Decode(File.ReadAllBytes(Asset("libavif_10bit_420_alpha.avif")));
        await Assert.That(img.HasAlpha).IsTrue();
        int w = (int)img.Columns, h = (int)img.Rows, ch = img.NumberOfChannels;
        var a = new byte[w * h * 2];
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                ushort v10 = (ushort)Math.Round(row[x * ch + ch - 1] * 1023.0 / 65535.0);   // back to the coded depth
                BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan((y * w + x) * 2), v10);
            }
        }
        await Assert.That(Hex(MD5.HashData(a))).IsEqualTo("c88079b009940171afbf84e8b48fcfd1");
    }

    // Planes as ffmpeg writes raw video: Y, U, V tightly packed; u16 LE for >8-bit, u8 otherwise.
    private static byte[] NativePlanes(DecodedVideoFrame f)
    {
        bool hbd = f.BitDepth > 8;
        int ssx = f.Format == PixelFormat.Yuv444P ? 0 : 1;
        int ssy = f.Format == PixelFormat.Yuv420P ? 1 : 0;
        int cw = (f.Width + ssx) >> ssx, chh = (f.Height + ssy) >> ssy;
        int bps = hbd ? 2 : 1;
        var o = new byte[(f.Width * f.Height + 2 * cw * chh) * bps];
        int p = 0;
        void Put(int pw, int ph, int stride, ReadOnlySpan<byte> b8, ReadOnlySpan<ushort> b16)
        {
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    if (hbd) { BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(p), b16[y * stride + x]); p += 2; }
                    else o[p++] = b8[y * stride + x];
                }
        }
        Put(f.Width, f.Height, f.YStride, f.YPlane.Span, f.YPlane16.Span);
        Put(cw, chh, f.UStride, f.UPlane.Span, f.UPlane16.Span);
        Put(cw, chh, f.VStride, f.VPlane.Span, f.VPlane16.Span);
        return o;
    }

    private static string Hex(byte[] h) => Convert.ToHexString(h).ToLowerInvariant();

    // Minimal ISOBMFF walk: meta/pitm -> primary item id, meta/iloc -> its (single-extent, file-offset) data.
    private static byte[] PrimaryItemData(byte[] d)
    {
        int meta = FindBox(d, 0, d.Length, "meta");
        int metaEnd = meta + (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(meta));
        int pitm = FindBox(d, meta + 12, metaEnd, "pitm");
        int primary = d[pitm + 8] == 0 ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pitm + 12))
                                       : (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pitm + 12));
        int iloc = FindBox(d, meta + 12, metaEnd, "iloc");
        int ver = d[iloc + 8], p = iloc + 12;
        int offSz = d[p] >> 4, lenSz = d[p] & 15, baseSz = d[p + 1] >> 4, idxSz = ver >= 1 ? d[p + 1] & 15 : 0;
        p += 2;
        int count = ver < 2 ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p)) : (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
        p += ver < 2 ? 2 : 4;
        for (int i = 0; i < count; i++)
        {
            int id = ver < 2 ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p)) : (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            p += ver < 2 ? 2 : 4;
            if (ver >= 1) p += 2;
            p += 2;
            long baseOff = ReadN(d, p, baseSz); p += baseSz;
            int ext = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p)); p += 2;
            using var ms = new MemoryStream();
            for (int e = 0; e < ext; e++)
            {
                p += idxSz;
                long off = ReadN(d, p, offSz); p += offSz;
                long len = ReadN(d, p, lenSz); p += lenSz;
                ms.Write(d, (int)(baseOff + off), (int)len);
            }
            if (id == primary) return ms.ToArray();
        }
        throw new InvalidDataException("primary item not found");
    }

    private static long ReadN(byte[] d, int p, int n)
    {
        long v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | d[p + i];
        return v;
    }

    private static int FindBox(byte[] d, int start, int end, string type)
    {
        for (int p = start; p + 8 <= end;)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            if (System.Text.Encoding.ASCII.GetString(d, p + 4, 4) == type) return p;
            if (size < 8) break;
            p += size;
        }
        throw new InvalidDataException($"box {type} not found");
    }
}
