using System;
using System.IO;
using System.Security.Cryptography;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Tests.Formats;

// AVIF container metadata parity with libavif: ICC profiles (colr 'prof'), checked against a libavif-made file
// (Pillow 12 / libavif 1.3) and through our own encode -> decode -> other-format round trips.
public sealed class AvifMetadataTests
{
    private static string Asset(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name);

    private static ImageFrame Gradient(int w, int h, bool alpha = false)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * ch] = (ushort)(x * 65535 / (w - 1));
                row[x * ch + 1] = (ushort)(y * 65535 / (h - 1));
                row[x * ch + 2] = 30000;
                if (alpha) row[x * ch + 3] = (ushort)(20000 + x * 300);
            }
        }
        return f;
    }

    private static byte[] FakeIcc(int n)
    {
        var icc = new byte[n];
        for (int i = 0; i < n; i++) icc[i] = (byte)(i * 7 + 3);
        icc[36] = (byte)'a'; icc[37] = (byte)'c'; icc[38] = (byte)'s'; icc[39] = (byte)'p';
        return icc;
    }

    [Test]
    public async Task Icc_FromLibavifFile_IsExtractedExactly()
    {
        // Lab profile written by libavif (Pillow) on a colour+alpha image; libavif pairs it with nclx 2/2/6.
        var img = HeifCoder.Decode(File.ReadAllBytes(Asset("libavif_icc_lab_alpha.avif")));
        await Assert.That(img.IccProfile).IsNotNull();
        await Assert.That(Convert.ToHexString(MD5.HashData(img.IccProfile!)).ToLowerInvariant()).IsEqualTo("a105577d7621b29c0ca3b274619ad9db");
        await Assert.That(img.Metadata.IccProfile!.Data).IsEquivalentTo(img.IccProfile!);
        await Assert.That(img.Metadata.Cicp).IsEqualTo(new CicpInfo(2, 2, 6, true));
    }

    [Test]
    [Arguments(false, 8)]
    [Arguments(true, 10)]
    public async Task Icc_RoundTrips_AndReachesPng(bool alpha, int bd)
    {
        var src = Gradient(90, 60, alpha);
        byte[] icc = FakeIcc(3144);
        src.Metadata.IccProfile = new IccProfile(icc);
        var dec = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions { BitDepth = bd }));
        await Assert.That(dec.IccProfile).IsEquivalentTo(icc);

        // The decoded profile is carried by the shared metadata, so other encoders re-embed it (PNG iCCP).
        using var ms = new MemoryStream();
        PngCoder.Write(dec, ms);
        ms.Position = 0;
        var png = PngCoder.Read(ms);
        await Assert.That(png.Metadata.IccProfile!.Data).IsEquivalentTo(icc);
    }

    [Test]
    public async Task Icc_FromFrameProperty_IsWritten()
    {
        // JPEG XL decode populates ImageFrame.IccProfile (not Metadata); that slot is honoured too.
        var src = Gradient(64, 40);
        src.IccProfile = FakeIcc(500);
        await Assert.That(HeifCoder.Decode(HeifCoder.EncodeAvif(src)).IccProfile).IsEquivalentTo(src.IccProfile);
    }
}
