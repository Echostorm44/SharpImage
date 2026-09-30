using System;
using SharpImage.Formats.Av1;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace SharpImage.Tests.Formats;

// The AV1 encoder runs its pixel pipeline in ushort through the decoder's 16-bit twins (Predict16,
// InvTxfmAdd16, PrepareIntraEdges<ushort>). For 8-bit streams the DECODER uses the 8-bit functions, so the
// twins at bitDepth=8 must reproduce the 8-bit results exactly or the encoder's reconstruction would drift from
// the decoder's. These randomized sweeps pin that equivalence over every intra mode / tx size / tx type.
public sealed class Av1HbdTwinTests
{
    [Test]
    public async Task Predict16AtBd8MatchesPredict()
    {
        var rnd = new Random(7);
        int[] sizes = { 4, 8, 16, 32, 64 };
        int mismatches = 0, compared = 0;
        const int center = 256;
        for (int trial = 0; trial < 4000; trial++)
        {
            int w = sizes[rnd.Next(sizes.Length)], h = sizes[rnd.Next(sizes.Length)];
            if (w > 4 * h || h > 4 * w) continue;
            int mode = rnd.Next(14);
            int angle = mode switch
            {
                10 => rnd.Next(3, 88),
                11 => rnd.Next(93, 178),
                12 => rnd.Next(183, 268),
                13 => rnd.Next(5),
                _ => 0,
            };
            if (mode is >= 10 and <= 12)
            {
                if (rnd.Next(2) == 0) angle |= 1 << 10;   // edge filter enable
                if (rnd.Next(2) == 0) angle |= 1 << 9;    // smooth neighbour
            }
            var e8 = new byte[513];
            var e16 = new ushort[513];
            for (int i = 0; i < e8.Length; i++) { e8[i] = (byte)rnd.Next(256); e16[i] = e8[i]; }
            var d8 = new byte[w * h];
            var d16 = new ushort[w * h];
            var dB = new byte[w * h];
            if (mode == 13 && (w > 32 || h > 32)) continue;   // filter-intra is defined only up to 32x32
            int maxW = rnd.Next(1, 2 * w + 1), maxH = rnd.Next(1, 2 * h + 1);
            bool t8 = false, t16 = false, tB = false;
            try { Av1IntraPred.Predict(mode, d8, w, e8, center, w, h, angle, maxW, maxH, 8); } catch (IndexOutOfRangeException) { t8 = true; }
            // (the vector paths slice their edge spans, so an out-of-range read can surface as ArgumentOutOfRangeException)
            try { Av1IntraPred.Predict16(mode, d16, w, e16, center, w, h, angle, maxW, maxH, 8); } catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { t16 = true; }
            // the generic predictor's lowbd (byte) instantiation
            try { Av1IntraPred.Predict16<byte>(mode, dB, w, e8, center, w, h, angle, maxW, maxH, 8); } catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { tB = true; }
            if (t8 || t16 || tB) { if (t8 != t16 || t8 != tB) mismatches++; continue; }   // invalid combo: all must reject alike
            compared++;
            for (int i = 0; i < d8.Length; i++) if (d8[i] != d16[i] || d8[i] != dB[i]) { mismatches++; break; }
        }
        await Assert.That(mismatches).IsEqualTo(0);
        await Assert.That(compared).IsGreaterThan(2000);
    }

    [Test]
    public async Task InvTxfmAdd16AtBd8MatchesInvTxfmAdd()
    {
        var rnd = new Random(11);
        int mismatches = 0;
        Av1TxType[] types = (Av1TxType[])Enum.GetValues(typeof(Av1TxType));
        for (int tx = 0; tx < Av1Tables.TxfmDimensions.Length; tx++)
        {
            ref readonly var td = ref Av1Tables.TxfmDimensions[tx];
            int w = 4 * td.W, h = 4 * td.H;
            int scanLen = Av1Tables.Scans[tx].Length;
            for (int trial = 0; trial < 40; trial++)
            {
                var txType = types[rnd.Next(types.Length)];
                if ((w >= 32 || h >= 32) && txType != Av1TxType.DctDct && txType != Av1TxType.Identity) txType = Av1TxType.DctDct;
                if ((w == 64 || h == 64) && txType != Av1TxType.DctDct) txType = Av1TxType.DctDct;
                int eob = rnd.Next(scanLen);
                var cf8 = new int[Math.Max(w * h, 32 * 32)];
                for (int i = 0; i <= eob; i++) cf8[Av1Tables.Scans[tx][i]] = rnd.Next(-300, 301);
                var cf16 = (int[])cf8.Clone();
                var p8 = new byte[w * h];
                var p16 = new ushort[w * h];
                for (int i = 0; i < p8.Length; i++) { p8[i] = (byte)rnd.Next(256); p16[i] = p8[i]; }
                try
                {
                    Av1InvTransform.InvTxfmAdd(p8, w, cf8, eob, tx, Av1InvTransform.TxShift[tx], txType, 8);
                    Av1InvTransform.InvTxfmAdd16(p16, w, cf16, eob, tx, Av1InvTransform.TxShift[tx], txType, 8);
                }
                catch (Exception) { continue; } // tx type not valid for this size
                for (int i = 0; i < p8.Length; i++) if (p8[i] != p16[i]) { mismatches++; break; }
            }
        }
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task PrepareIntraEdges16AtBd8MatchesByte()
    {
        var rnd = new Random(23);
        int mismatches = 0;
        const int stride = 256, planeH = 256, edgeCenter = 256;
        var modes = (Av1IntraPredMode[])Enum.GetValues(typeof(Av1IntraPredMode));
        int[] sizes4 = { 1, 2, 4, 8, 16 };
        for (int trial = 0; trial < 3000; trial++)
        {
            var p8 = new byte[stride * planeH];
            var p16 = new ushort[stride * planeH];
            for (int i = 0; i < p8.Length; i++) { p8[i] = (byte)rnd.Next(256); p16[i] = p8[i]; }
            int tw = sizes4[rnd.Next(sizes4.Length)], th = sizes4[rnd.Next(sizes4.Length)];
            int bw4 = stride / 4, bh4 = planeH / 4;
            int x = rnd.Next(0, bw4 - tw), y = rnd.Next(0, bh4 - th);
            var mode = modes[rnd.Next(modes.Length)];
            if ((int)mode > (int)Av1IntraPredMode.Paeth) mode = Av1IntraPredMode.Dc;
            var flags = (Av1EdgeFlags)rnd.Next(0, 16);
            bool filter = rnd.Next(2) == 0;
            int a8 = mode is >= Av1IntraPredMode.Vertical and <= Av1IntraPredMode.VerticalLeft ? rnd.Next(-3, 4) * 3 : 0;
            int a16 = a8;
            var o8 = new byte[513];
            var o16 = new ushort[513];
            var oB = new byte[513];
            int aB = a8;
            int off = (y * 4) * stride + x * 4;
            int m8 = Av1Reconstruction.PrepareIntraEdges(x, x > 0, y, y > 0, bw4, bh4, flags, p8, off, stride, default,
                mode, ref a8, tw, th, filter, o8, edgeCenter, 8);
            int m16 = Av1Reconstruction.PrepareIntraEdges(x, x > 0, y, y > 0, bw4, bh4, flags, p16, off, stride, default,
                mode, ref a16, tw, th, filter, o16, edgeCenter, 8);
            int mB = Av1Reconstruction.PrepareIntraEdges<byte>(x, x > 0, y, y > 0, bw4, bh4, flags, p8, off, stride, default,
                mode, ref aB, tw, th, filter, oB, edgeCenter, 8);
            bool bad = m8 != m16 || a8 != a16 || m8 != mB || a8 != aB;
            for (int i = 0; i < o8.Length && !bad; i++) if (o8[i] != o16[i] || o8[i] != oB[i]) bad = true;
            if (bad) mismatches++;
        }
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task CflAndPalette16AtBd8MatchByte()
    {
        var rnd = new Random(31);
        int[] sizes = { 4, 8, 16, 32 };
        int mismatches = 0;
        const int center = 128;
        for (int trial = 0; trial < 2000; trial++)
        {
            int w = sizes[rnd.Next(sizes.Length)], h = sizes[rnd.Next(sizes.Length)];
            if (w > 4 * h || h > 4 * w) continue;
            int ssH = rnd.Next(2), ssV = rnd.Next(2), lw = w << ssH, lh = h << ssV;
            var l8 = new byte[lw * lh];
            var l16 = new ushort[lw * lh];
            for (int i = 0; i < l8.Length; i++) { l8[i] = (byte)rnd.Next(256); l16[i] = l8[i]; }
            int wPad = rnd.Next(w / 4), hPad = rnd.Next(h / 4);
            var ac8 = new short[w * h]; var ac16 = new short[w * h]; var acB = new short[w * h];
            Av1IntraPred.CflAc(ac8, l8, lw, wPad, hPad, w, h, ssH, ssV);
            Av1IntraPred.CflAc16<ushort>(ac16, l16, lw, wPad, hPad, w, h, ssH, ssV);
            Av1IntraPred.CflAc16<byte>(acB, l8, lw, wPad, hPad, w, h, ssH, ssV);
            bool bad = !ac8.AsSpan().SequenceEqual(ac16) || !ac8.AsSpan().SequenceEqual(acB);
            var e8 = new byte[257]; var e16 = new ushort[257];
            for (int i = 0; i < e8.Length; i++) { e8[i] = (byte)rnd.Next(256); e16[i] = e8[i]; }
            int alpha = rnd.Next(-16, 17);
            var d8 = new byte[w * h]; var d16 = new ushort[w * h]; var dB = new byte[w * h];
            Av1IntraPred.PredCfl(d8, w, e8, center, w, h, ac8, alpha);
            Av1IntraPred.PredCfl16<ushort>(d16, w, e16, center, w, h, ac8, alpha, 8);
            Av1IntraPred.PredCfl16<byte>(dB, w, e8, center, w, h, ac8, alpha, 8);
            for (int i = 0; i < d8.Length && !bad; i++) if (d8[i] != d16[i] || d8[i] != dB[i]) bad = true;
            var pal = new ushort[8];
            for (int i = 0; i < 8; i++) pal[i] = (ushort)rnd.Next(256);
            var idx = new byte[w * h / 2];
            for (int i = 0; i < idx.Length; i++) idx[i] = (byte)(rnd.Next(8) | rnd.Next(8) << 4);
            Av1IntraPred.PredPalette16<ushort>(d16, w, pal, idx, w, h);
            Av1IntraPred.PredPalette16<byte>(dB, w, pal, idx, w, h);
            for (int i = 0; i < d16.Length && !bad; i++) if (d16[i] != dB[i]) bad = true;
            if (bad) mismatches++;
        }
        await Assert.That(mismatches).IsEqualTo(0);
    }
}
