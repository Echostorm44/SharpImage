using System;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// avifenc --creation-time / --modification-time: written into mvhd / tkhd / mdhd (seconds since 1904), read back by
// DecodeSequence; unset stays 0 / null.
public sealed class AvifSequenceTimeTests
{
    private static ImageSequence TwoFrames()
    {
        var seq = new ImageSequence();
        for (int i = 0; i < 2; i++)
        {
            var f = new ImageFrame();
            f.Initialize(32, 24, ColorspaceType.SRGB, false);
            for (int y = 0; y < 24; y++) { var row = f.GetPixelRowForWrite(y); for (int x = 0; x < 32 * 3; x++) row[x] = (ushort)((x * 997 + y * 131 + i * 9000) & 0xFFFF); }
            f.Delay = 10;
            seq.AddFrame(f);
        }
        return seq;
    }

    [Test]
    public async Task CreationAndModificationTimesRoundTrip()
    {
        using var seq = TwoFrames();
        var created = new DateTimeOffset(2024, 5, 17, 12, 30, 0, TimeSpan.Zero);
        var modified = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        byte[] avif = HeifCoder.EncodeAvifSequence(seq, new AvifEncodeOptions { Quality = 60, CreationTime = created, ModificationTime = modified });
        using var back = HeifCoder.DecodeSequence(avif);
        await Assert.That(back.CreationTime).IsEqualTo(created);
        await Assert.That(back.ModificationTime).IsEqualTo(modified);

        byte[] plain = HeifCoder.EncodeAvifSequence(seq, new AvifEncodeOptions { Quality = 60 });
        using var back2 = HeifCoder.DecodeSequence(plain);
        await Assert.That(back2.CreationTime).IsNull();
    }
}
