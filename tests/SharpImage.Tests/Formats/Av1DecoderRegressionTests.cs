using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SharpImage.Formats;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Decoder bugs found by the conformance sweeps (libavif test data vs dav1d, Argon), pinned individually.
public sealed class Av1DecoderRegressionTests
{
    // 8-bit inverse transforms clamp the column-pass input to 16 bits (spec: Max(BitDepth + 6, 16)), not BitDepth + 6:
    // a saturated DC (dequantized 32767) through ADST_ADST 4x4 must give dav1d's ee ff ff ff / ff... (Argon test12207).
    [Test]
    public async Task InverseTransform_8Bit_ColumnClampIs16Bits()
    {
        var dst = new ushort[16];
        ushort[] pred = [30, 30, 30, 30, 30, 30, 30, 30, 47, 30, 30, 30, 30, 47, 30, 30];
        pred.CopyTo(dst, 0);
        var cf = new int[1024];
        cf[0] = 32767;
        cf[4] = -285;
        Av1InvTransform.InvTxfmAdd16(dst, 4, cf, 1, 0, Av1InvTransform.TxShift[0], Av1TxType.AdstAdst, 8);
        await Assert.That(dst[0]).IsEqualTo((ushort)0xee);
        await Assert.That(dst.Skip(1).All(v => v == 0xff)).IsTrue();
    }

    // A luma palette block overhanging the frame bottom with CfL chroma: the palette index map past the visible rows
    // repeats the last visible row (dav1d pal_idx_finish), so CfL's block average matches. libavif's
    // colors_text_hdr_srgb.avif item 4 (10-bit 4:4:4): SHA-256 of dav1d's planes (u16 LE).
    [Test]
    public async Task PaletteBlockPastFrameEdge_CflMatchesDav1d()
    {
        var c = HeifContainer.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", "libavif_colors_text_hdr_srgb.avif")));
        using var f = new Av1Decoder().Decode(c.ItemData(4)!, 0, true)!;
        var buf = new byte[200 * 200 * 3 * 2];
        int o = 0;
        foreach (var (plane, stride) in new[] { (f.YPlane16, f.YStride), (f.UPlane16, f.UStride), (f.VPlane16, f.VStride) })
            for (int y = 0; y < 200; y++)
                for (int x = 0; x < 200; x++) { ushort v = plane.Span[y * stride + x]; buf[o++] = (byte)v; buf[o++] = (byte)(v >> 8); }
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(buf))[..16]).IsEqualTo("431bf4538c34c776");
    }
}
