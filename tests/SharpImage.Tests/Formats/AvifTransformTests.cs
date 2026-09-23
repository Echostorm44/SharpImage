using System;
using System.IO;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Metadata;
using SharpImage.Transform;

namespace SharpImage.Tests.Formats;

// HEIF transformative properties (clap, irot, imir) and pasp. Decode applies them in MIAF order (clap -> irot ->
// imir) so the result is display-oriented; encode signals them from the EXIF-style Orientation (libavif's
// avifImageExtractExifOrientationToIrotImir mapping) or explicit options. Cross-checked against libavif's own test
// files and, for all eight orientations, against Pillow/libavif (see the dev harness Av1HbdVerify.Meta).
public sealed class AvifTransformTests
{
    private static string Asset(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", name);

    // Smooth but asymmetric content (every rotation/mirror is distinguishable, and it survives lossy coding).
    private static ImageFrame Asym(int w, int h, bool alpha = false)
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
                row[x * ch + 2] = (ushort)(x * y * 65535 / ((w - 1) * (h - 1)));
                if (alpha) row[x * ch + 3] = (ushort)(65535 - x * 400);
            }
        }
        return f;
    }

    private static double MaxDiff(ImageFrame a, ImageFrame b, int chans)
    {
        double m = 0;
        for (long y = 0; y < a.Rows; y++)
            for (long x = 0; x < a.Columns; x++)
                for (int c = 0; c < chans; c++) m = Math.Max(m, Math.Abs(a.GetPixelChannel(x, y, c) - b.GetPixelChannel(x, y, c)));
        return m / 257.0;
    }

    [Test]
    [Arguments(OrientationType.TopRight)]
    [Arguments(OrientationType.BottomRight)]
    [Arguments(OrientationType.BottomLeft)]
    [Arguments(OrientationType.LeftTop)]
    [Arguments(OrientationType.RightTop)]
    [Arguments(OrientationType.RightBottom)]
    [Arguments(OrientationType.LeftBottom)]
    public async Task Orientation_IsSignalled_AndDecodesDisplayOriented(OrientationType o)
    {
        var src = Asym(48, 32, alpha: true);
        src.Orientation = o;
        var dec = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 0, BitDepth = 10, ChromaSubsampling = AvifChromaSubsampling.Yuv444 }));
        var expect = Geometry.AutoOrient(src);
        await Assert.That(dec.Columns).IsEqualTo(expect.Columns);
        await Assert.That(dec.Rows).IsEqualTo(expect.Rows);
        await Assert.That(dec.Orientation).IsEqualTo(OrientationType.TopLeft);
        await Assert.That(MaxDiff(dec, expect, 4)).IsLessThan(3.0);   // near-lossless: any wrong transform is ~100+
    }

    [Test]
    public async Task ExplicitRotationAndMirror_OverrideOrientation()
    {
        var src = Asym(40, 24);
        src.Orientation = OrientationType.RightTop;
        // irot 1 (90° anti-clockwise) then imir 1 (left/right) is EXIF orientation 7 (libavif's mapping).
        var dec = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 0, Rotation = 1, Mirror = 1, BitDepth = 10, ChromaSubsampling = AvifChromaSubsampling.Yuv444 }));
        src.Orientation = OrientationType.RightBottom;
        var expect = Geometry.AutoOrient(src);
        await Assert.That(MaxDiff(dec, expect, 3)).IsLessThan(3.0);
    }

    [Test]
    public async Task CropRect_IsWrittenAsClap_AndApplied()
    {
        var src = Asym(96, 64);
        var full = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 0, BitDepth = 10, ChromaSubsampling = AvifChromaSubsampling.Yuv444 }));
        var dec = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 0, BitDepth = 10, ChromaSubsampling = AvifChromaSubsampling.Yuv444, CropRect = (10, 7, 51, 33) }));
        await Assert.That((dec.Columns, dec.Rows)).IsEqualTo((51L, 33L));
        await Assert.That(MaxDiff(dec, Geometry.Crop(full, 10, 7, 51, 33), 3)).IsEqualTo(0.0);
    }

    [Test]
    public async Task LibavifIrot_AppliesToColourAndAlpha()
    {
        // abc.png encoded by libavif with irot on colour and alpha; the NOirot variant (alpha lacks the association)
        // decodes identically, as in libavif.
        var a = HeifCoder.Decode(File.ReadAllBytes(Asset("libavif_abc_color_irot_alpha_irot.avif")));
        var b = HeifCoder.Decode(File.ReadAllBytes(Asset("libavif_abc_color_irot_alpha_NOirot.avif")));
        await Assert.That((a.Columns, a.Rows)).IsEqualTo((256L, 512L));
        await Assert.That(a.HasAlpha).IsTrue();
        await Assert.That(MaxDiff(a, b, 4)).IsEqualTo(0.0);
    }

    [Test]
    public async Task LibavifUnknownProperties_AreIgnored_TransformsApplied()
    {
        // 12x34 with essential irot (angle 1) plus made-up non-essential 'clop' / 'imor' — only the rotation applies.
        var img = HeifCoder.Decode(File.ReadAllBytes(Asset("libavif_clop_irot_imor.avif")));
        await Assert.That((img.Columns, img.Rows)).IsEqualTo((34L, 12L));
        // Non-essential clap/irot/imir (libavif rejects the file); we decode leniently: crop (4,6,8x10), rotate, mirror.
        var lenient = HeifCoder.Decode(File.ReadAllBytes(Asset("libavif_clap_irot_imir_non_essential.avif")));
        await Assert.That((lenient.Columns, lenient.Rows)).IsEqualTo((10L, 8L));
    }

    [Test]
    public async Task Pasp_RoundTrips_AndExifOrientationIsNormalised()
    {
        var src = Asym(40, 24);
        src.Metadata.PixelAspectRatio = new PixelAspectRatio(4, 3);
        var exif = new ExifProfile();
        exif.SetTag(new ExifEntry { Tag = ExifTag.Orientation, DataType = ExifDataType.Short, Count = 1, Value = [6, 0] });
        src.Metadata.ExifProfile = exif;
        src.Orientation = OrientationType.RightTop;
        var dec = HeifCoder.Decode(HeifCoder.EncodeAvif(src));
        await Assert.That(dec.Metadata.PixelAspectRatio).IsEqualTo(new PixelAspectRatio(4, 3));
        await Assert.That((dec.Columns, dec.Rows)).IsEqualTo((24L, 40L));
        // Pixels are now display-oriented, so the EXIF tag must no longer ask for a rotation.
        await Assert.That(dec.Metadata.ExifProfile!.GetTag(ExifTag.Orientation)!.Value.GetUInt16(dec.Metadata.ExifProfile.IsLittleEndian)).IsEqualTo((ushort)1);
    }
}
