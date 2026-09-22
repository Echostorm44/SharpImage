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
    // Odd picture sizes with deblock + CDEF: the loop filter must use ceil(dim/4) (dav1d w4/h4), not the MI grid.
    [Arguments("libavif_10bit_420_257x131_lf_cdef.avif", 257, 131, "ee60cf6338c4bf402de5237263d49b94")]
    [Arguments("sharpimage_8bit_420_257x131_lf_cdef.avif", 257, 131, "7d0e70c2a59725c98f7746ca477e7b98")]
    [Arguments("sharpimage_8bit_420_129x67_lf_cdef.avif", 129, 67, "db65513a81a3faf76dcc1ffcf3fb2028")]
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

    // RGB conversion parity with libavif (avifImageYUVToRGB, default AUTOMATIC upsampling): sampled reference pixels
    // from libavif 1.4.2's own decode, at the coded depth. HeifCoder ports libavif's reference YUV->RGB path
    // (bilinear 9/3/3/1 chroma, unorm float tables, matrix table), so it must land within one code.
    [Test]
    [Arguments("libavif_10bit_420_qm_dq.avif", 10, "(130,379,727,706,564), (183,353,874,834,618), (14,238,564,556,506), (127,332,741,721,580), (26,80,708,690,580), (57,190,770,752,618), (240,126,916,870,586), (194,278,1019,988,715), (52,293,733,713,591), (127,6,952,916,691), (110,208,646,632,534), (143,93,1022,1023,824), (199,81,1023,1023,1022), (36,71,714,695,585), (227,64,1021,1022,777), (67,0,715,705,599), (2,107,626,618,545), (110,84,943,908,717), (85,148,754,727,602), (160,101,1021,1021,1021), (104,93,1021,1020,885), (100,196,653,636,549), (152,11,1023,1022,888), (184,212,1023,1023,1015), (0,0,797,774,636), (255,383,554,529,385), (255,0,1015,1023,757), (0,383,462,463,457)")]
    [Arguments("libavif_10bit_420_257x131_lf_cdef.avif", 10, "(65,183,694,146,472), (7,238,804,57,376), (63,26,162,877,884), (40,57,239,881,831), (95,240,926,904,287), (63,194,729,199,441), (26,127,457,487,656), (3,110,371,876,726), (104,143,605,238,539), (46,199,723,99,443), (40,36,171,961,886), (35,227,806,161,381), (32,67,266,886,812), (0,2,0,534,1013), (53,110,435,308,676), (42,85,336,660,754), (74,160,626,99,518), (50,104,410,392,696), (46,100,391,482,713), (98,152,630,241,522), (5,184,622,249,524), (106,84,409,58,690), (37,135,499,285,624), (16,169,584,246,554), (0,0,0,521,1015), (130,256,1015,867,214), (130,0,160,329,884), (0,256,855,76,338)")]
    [Arguments("libavif_12bit_444_qm_dq.avif", 12, "(130,379,2919,2834,2279), (183,353,3490,3338,2454), (14,238,2250,2223,2032), (127,332,2975,2893,2325), (26,80,2831,2769,2350), (57,190,3105,3020,2484), (240,126,3729,3497,2389), (194,278,4063,3971,2864), (52,293,2951,2878,2381), (127,6,3816,3661,2757), (110,208,2568,2517,2140), (143,93,4095,4091,3295), (199,81,4092,4094,4095), (36,71,2857,2788,2353), (227,64,4093,4088,3102), (67,0,2858,2796,2389), (2,107,2509,2456,2178), (110,84,3744,3614,2854), (85,148,3050,2949,2467), (160,101,4095,4094,4059), (104,93,4095,4095,3491), (100,196,2589,2527,2172), (152,11,4092,4085,3575), (184,212,4095,4093,4095), (0,0,3170,3074,2524), (255,383,2194,2100,1546), (255,0,4095,4095,2983), (0,383,1831,1840,1819)")]
    public async Task RgbMatchesLibavif(string file, int bd, string refs)
    {
        var img = HeifCoder.Decode(File.ReadAllBytes(Asset(file)));
        int ch = img.NumberOfChannels, max = (1 << bd) - 1, worst = 0;
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(refs, @"\((\d+),(\d+),(\d+),(\d+),(\d+)\)"))
        {
            var g = ((System.Text.RegularExpressions.Match)m).Groups;
            int y = int.Parse(g[1].Value), x = int.Parse(g[2].Value);
            var row = img.GetPixelRow(y);
            for (int c = 0; c < 3; c++)
            {
                int ours = (int)Math.Round(row[x * ch + c] * (double)max / 65535.0);
                worst = Math.Max(worst, Math.Abs(ours - int.Parse(g[3 + c].Value)));
            }
        }
        await Assert.That(worst).IsLessThanOrEqualTo(1);
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
