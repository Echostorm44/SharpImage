using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SharpImage.Formats;
using SharpImage.Formats.Av1;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Encoder loop restoration: the operation log the tile is replayed from reproduces the coder exactly (through RD
// trial rollbacks and the winning-tail copy), and a photo at the default speed carries Wiener / self-guided units
// (bit-exact against libdav1d was checked with the encdump probe over 8/10/12-bit, 4:2:0/4:2:2/4:4:4, odd sizes and
// multi-tile frames).
public sealed class AvifLoopRestorationTests
{
    [Test]
    public async Task ReplayOfLoggedOperationsReproducesTheCoder()
    {
        var rnd = new Random(3);
        var w = new Av1MsacWriter { Log = new() };
        var cdf = new ushort[] { 30000, 20000, 9000, 0, 0 };
        var bcdf = new ushort[] { 16000, 0 };
        for (int i = 0; i < 4000; i++)
        {
            // an RD-style trial: encode a few symbols, keep either the trial or a second variant
            var snap = w.Save();
            int k = rnd.Next(1, 6);
            for (int j = 0; j < k; j++) w.EncodeSymbolAdapt(cdf, rnd.Next(4), 3);
            var afterFirst = w.Save();
            var tail = w.PrecarryFrom(snap.PrecarryCount);
            var logTail = w.LogFrom(snap.LogCount);
            w.Restore(snap);
            w.EncodeBoolAdapt(bcdf, (uint)rnd.Next(2));
            w.EncodeLiteral((uint)rnd.Next(16), 4);
            if (rnd.Next(2) == 0)
            {
                // commit the first variant instead: rewind and re-apply its state and tails (EncodePartitionColorTrueRd)
                w.Restore(snap);
                w.Restore(afterFirst);
                w.AppendPrecarry(tail);
                w.AppendLog(logTail);
            }
            if (rnd.Next(50) == 0) w.Mark(i);
        }
        var log = w.Log!;
        byte[] direct = w.Finish();
        byte[] replayed = Av1MsacWriter.Replay(log, null);
        await Assert.That(replayed.AsSpan().SequenceEqual(direct)).IsTrue();
    }

    private static (Av1RestorationType Y, Av1RestorationType U, Av1RestorationType V) LrTypes(byte[] avif)
    {
        var c = HeifContainer.Parse(avif);
        var dec = new Av1Decoder();
        using var frame = dec.Decode(c.ItemData(c.PrimaryId)!, 0, isKeyframe: true);
        var ctx = typeof(Av1Decoder).GetField("ctx", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dec)!;
        var fh = (Av1DecoderFrameHeader)ctx.GetType().GetField("FrameHeader")!.GetValue(ctx)!;
        return (fh.GetLrType(0), fh.GetLrType(1), fh.GetLrType(2));
    }

    [Test]
    public async Task PhotoUsesRestorationAtDefaultSpeedOnly()
    {
        var src = FormatRegistry.Read(Path.Combine(AppContext.BaseDirectory, "TestAssets", "peppers.jpg"));
        byte[] s6 = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Quality = 50 });
        byte[] s7 = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Quality = 50, Speed = 7 });
        var t6 = LrTypes(s6);
        await Assert.That(t6.Y != Av1RestorationType.None || t6.U != Av1RestorationType.None || t6.V != Av1RestorationType.None).IsTrue();
        var t7 = LrTypes(s7);
        await Assert.That(t7.Y == Av1RestorationType.None && t7.U == Av1RestorationType.None && t7.V == Av1RestorationType.None).IsTrue();
        using var back = HeifCoder.Decode(s6);
        await Assert.That(back.Columns).IsEqualTo(src.Columns);
    }
}
