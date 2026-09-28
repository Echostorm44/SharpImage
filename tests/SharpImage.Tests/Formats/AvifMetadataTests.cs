using System;
using System.IO;
using System.Security.Cryptography;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Tests.Formats;

// AVIF container metadata parity with libavif: ICC profiles (colr 'prof') and Exif / XMP items (cdsc), checked
// against libavif-made files (Pillow 12 / libavif 1.3) and through our own encode -> decode -> other-format round trips.
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

    private const string Xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Parity</dc:title></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    [Test]
    public async Task ExifAndXmp_FromLibavifFile_AreExtracted()
    {
        var img = HeifCoder.Decode(File.ReadAllBytes(Asset("libavif_exif_xmp.avif")));
        var exif = img.Metadata.ExifProfile!;
        await Assert.That(exif.GetTag(ExifTag.Make)!.Value.GetString()).IsEqualTo("SharpCam");
        await Assert.That(exif.GetTag(ExifTag.Model)!.Value.GetString()).IsEqualTo("Model X-1");
        await Assert.That(exif.GetTag(ExifTag.DateTime)!.Value.GetString()).IsEqualTo("2026:09:22 12:34:56");
        await Assert.That(img.Metadata.Xmp).IsEqualTo(Xmp);
    }

    [Test]
    public async Task ExifAndXmp_RoundTrip_AndReachJpeg()
    {
        var withMeta = HeifCoder.Decode(File.ReadAllBytes(Asset("libavif_exif_xmp.avif")));
        var src = Gradient(80, 48, alpha: true);
        src.Metadata.ExifProfile = withMeta.Metadata.ExifProfile;
        src.Metadata.Xmp = Xmp;
        var dec = HeifCoder.Decode(HeifCoder.EncodeAvif(src));
        await Assert.That(dec.Metadata.ExifProfile!.GetTag(ExifTag.Model)!.Value.GetString()).IsEqualTo("Model X-1");
        await Assert.That(dec.Metadata.Xmp).IsEqualTo(Xmp);

        using var ms = new MemoryStream();
        JpegCoder.Write(dec, ms);
        ms.Position = 0;
        var jpg = JpegCoder.Read(ms);
        await Assert.That(jpg.Metadata.ExifProfile!.GetTag(ExifTag.Make)!.Value.GetString()).IsEqualTo("SharpCam");
        await Assert.That(jpg.Metadata.Xmp).IsEqualTo(Xmp);
    }

    [Test]
    public async Task HdrMetadata_ClliAndMdcv_RoundTrip()
    {
        // HDR10 example: BT.2020 primaries (G, B, R), D65, 1000 / 0.005 cd/m² mastering, MaxCLL 1000 / MaxFALL 400.
        // (ffprobe reads the same file back as "Mastering display" / "Content light level" side data.)
        var mdcv = new MasteringDisplayColourVolume(8500, 39850, 6550, 2300, 35400, 14600, 15635, 16450, 10000000, 50);
        var cll = new ContentLightLevel(1000, 400);
        var src = Gradient(64, 40);
        var dec = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions
        {
            BitDepth = 10, ColorPrimaries = 9, TransferCharacteristics = 16, MatrixCoefficients = 9,
            ContentLightLevel = cll, MasteringDisplay = mdcv,
        }));
        await Assert.That(dec.Metadata.ContentLightLevel).IsEqualTo(cll);
        await Assert.That(dec.Metadata.MasteringDisplay).IsEqualTo(mdcv);
        await Assert.That(dec.Metadata.Cicp).IsEqualTo(new CicpInfo(9, 16, 9, true));

        // Carried by metadata into a re-encode, and absent when never set.
        var again = HeifCoder.Decode(HeifCoder.EncodeAvif(dec, new AvifEncodeOptions { BitDepth = 10 }));
        await Assert.That(again.Metadata.MasteringDisplay).IsEqualTo(mdcv);
        await Assert.That(HeifCoder.Decode(HeifCoder.EncodeAvif(src)).Metadata.ContentLightLevel).IsNull();
    }

    [Test]
    public async Task PremultipliedAlpha_IsSignalled_AndUndoneOnDecode()
    {
        var src = Gradient(80, 48, alpha: true);
        byte[] prem = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { PremultiplyAlpha = true, Qp = 4 });
        byte[] plain = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 4 });
        static bool HasPrem(byte[] f) { for (int i = 0; i + 4 < f.Length; i++) if (f[i] == 'p' && f[i + 1] == 'r' && f[i + 2] == 'e' && f[i + 3] == 'm') return true; return false; }
        await Assert.That(HasPrem(prem)).IsTrue();
        await Assert.That(HasPrem(plain)).IsFalse();
        // Decoded colour is straight (un-premultiplied) again: close to the source where alpha is high.
        var dec = HeifCoder.Decode(prem);
        double maxErr = 0;
        for (int y = 0; y < 48; y++)
        {
            var r0 = src.GetPixelRow(y); var r1 = dec.GetPixelRow(y);
            for (int x = 0; x < 80; x++)
                if (r0[x * 4 + 3] > 40000)
                    for (int c = 0; c < 3; c++) maxErr = Math.Max(maxErr, Math.Abs(r0[x * 4 + c] - r1[x * 4 + c]) / 257.0);
        }
        await Assert.That(maxErr).IsLessThan(12);
    }

    [Test]
    public async Task Icc_FromFrameProperty_IsWritten()
    {
        // JPEG XL decode populates ImageFrame.IccProfile (not Metadata); that slot is honoured too.
        var src = Gradient(64, 40);
        src.IccProfile = FakeIcc(500);
        await Assert.That(HeifCoder.Decode(HeifCoder.EncodeAvif(src)).IccProfile).IsEquivalentTo(src.IccProfile);
    }

    [Test]
    public async Task Icc_WhoseColourSpaceContradictsTheOutput_IsDropped()
    {
        // avifenc refuses a colour ICC on a 4:0:0 encode (and a gray one on a colour encode) unless --ignore-icc:
        // the mismatched profile is dropped and the CICP falls back to sRGB; a matching one is kept.
        var src = Gradient(64, 40);
        byte[] rgb = FakeIcc(500), gray = FakeIcc(500);
        "RGB "u8.CopyTo(rgb.AsSpan(16)); "GRAY"u8.CopyTo(gray.AsSpan(16));
        var mono = new AvifEncodeOptions { ChromaSubsampling = AvifChromaSubsampling.Yuv400 };
        src.IccProfile = rgb;
        await Assert.That(HeifCoder.Decode(HeifCoder.EncodeAvif(src, mono)).IccProfile).IsNull();
        await Assert.That(HeifCoder.Decode(HeifCoder.EncodeAvif(src)).IccProfile).IsEquivalentTo(rgb);
        src.IccProfile = gray;
        await Assert.That(HeifCoder.Decode(HeifCoder.EncodeAvif(src, mono)).IccProfile).IsEquivalentTo(gray);
        await Assert.That(HeifCoder.Decode(HeifCoder.EncodeAvif(src)).IccProfile).IsNull();
    }
}
