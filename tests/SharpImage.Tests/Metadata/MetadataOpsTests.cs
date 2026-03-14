using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Tests.Metadata;

/// <summary>
/// Tests for XMP, IPTC, and combined metadata operations (strip-all, clone).
/// </summary>
public class MetadataOpsTests
{
    private static ImageFrame CreateSmallTestImage()
    {
        var img = new ImageFrame();
        img.Initialize(16, 16);
        for (int y = 0; y < 16; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < 16; x++)
            {
                int offset = x * 3;
                row[offset] = (ushort)(x * 4096);
                row[offset + 1] = (ushort)(y * 4096);
                row[offset + 2] = 32768;
            }
        }
        return img;
    }

    [Test]
    public async Task XmpMetadata_PngRoundTrip_Preserves()
    {
        var image = CreateSmallTestImage();
        string xmpData = @"<?xpacket begin=""﻿"" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/"">
  <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
    <rdf:Description rdf:about="""" xmlns:dc=""http://purl.org/dc/elements/1.1/"">
      <dc:title>SharpImage Test</dc:title>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end=""w""?>";
        image.Metadata.Xmp = xmpData;

        string tempPath = Path.Combine(Path.GetTempPath(), $"xmp_roundtrip_{Guid.NewGuid()}.png");
        try
        {
            FormatRegistry.Write(image, tempPath);
            var reloaded = FormatRegistry.Read(tempPath);

            await Assert.That(reloaded.Metadata.Xmp).IsNotNull();
            await Assert.That(reloaded.Metadata.Xmp).Contains("SharpImage Test");
            await Assert.That(reloaded.Metadata.Xmp).Contains("xmpmeta");

            reloaded.Dispose();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        image.Dispose();
    }

    [Test]
    public async Task ImageMetadata_HasMetadata_ReportsCorrectly()
    {
        var meta = new ImageMetadata();
        await Assert.That(meta.HasMetadata).IsFalse();

        meta.Xmp = "<xmp>test</xmp>";
        await Assert.That(meta.HasMetadata).IsTrue();

        meta.Xmp = null;
        await Assert.That(meta.HasMetadata).IsFalse();

        meta.ExifProfile = new ExifProfile();
        await Assert.That(meta.HasMetadata).IsTrue();
    }

    [Test]
    public async Task ImageMetadata_Clone_CreatesDeepCopy()
    {
        var meta = new ImageMetadata();
        meta.Xmp = "<xmp>original</xmp>";
        meta.ExifProfile = new ExifProfile();
        byte[] makeBytes = System.Text.Encoding.ASCII.GetBytes("TestCam\0");
        meta.ExifProfile.SetTag(new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)makeBytes.Length,
            Value = makeBytes
        });

        var clone = meta.Clone();
        clone.Xmp = "<xmp>modified</xmp>";
        clone.ExifProfile!.RemoveTag(ExifTag.Make);

        // Original should be unaffected
        await Assert.That(meta.Xmp).Contains("original");
        await Assert.That(meta.ExifProfile.GetTag(ExifTag.Make)).IsNotNull();
    }

    [Test]
    public async Task StripAllMetadata_RemovesEverything()
    {
        var image = CreateSmallTestImage();
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.Xmp = "<xmp>test</xmp>";
        image.Metadata.IptcProfile = new IptcProfile();
        image.Metadata.IptcProfile.SetValue(IptcDataSet.Headline, "Test");

        await Assert.That(image.Metadata.HasMetadata).IsTrue();

        image.Metadata.ExifProfile = null;
        image.Metadata.IccProfile = null;
        image.Metadata.Xmp = null;
        image.Metadata.IptcProfile = null;

        await Assert.That(image.Metadata.HasMetadata).IsFalse();
        image.Dispose();
    }

    [Test]
    public async Task IptcProfile_SetAndGetValue_Works()
    {
        var profile = new IptcProfile();
        profile.SetValue(IptcDataSet.Headline, "Breaking News");
        profile.SetValue(IptcDataSet.Caption, "This is the caption");
        profile.SetValue(IptcDataSet.Byline, "Test Author");

        await Assert.That(profile.GetValue(IptcDataSet.Headline)).IsEqualTo("Breaking News");
        await Assert.That(profile.GetValue(IptcDataSet.Caption)).IsEqualTo("This is the caption");
        await Assert.That(profile.GetValue(IptcDataSet.Byline)).IsEqualTo("Test Author");
    }

    [Test]
    public async Task IptcProfile_Records_ContainsAllSetValues()
    {
        var profile = new IptcProfile();
        profile.SetValue(IptcDataSet.City, "San Francisco");
        profile.SetValue(IptcDataSet.Keywords, "test,image,metadata");

        await Assert.That(profile.Records.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task IptcDataSet_GetName_ReturnsKnownNames()
    {
        await Assert.That(IptcDataSet.GetName(IptcDataSet.Headline)).IsEqualTo("Headline");
        await Assert.That(IptcDataSet.GetName(IptcDataSet.Caption)).IsEqualTo("Caption");
        await Assert.That(IptcDataSet.GetName(IptcDataSet.Byline)).IsEqualTo("Byline");
    }

    [Test]
    public async Task AllMetadataTypes_PngRoundTrip_PreservesBoth()
    {
        var image = CreateSmallTestImage();

        // Set EXIF
        image.Metadata.ExifProfile = new ExifProfile { IsLittleEndian = true };
        image.Metadata.ExifProfile.RawData = null;
        byte[] softBytes = System.Text.Encoding.ASCII.GetBytes("SharpImage\0");
        image.Metadata.ExifProfile.SetTag(new ExifEntry
        {
            Tag = ExifTag.Software,
            DataType = ExifDataType.Ascii,
            Count = (uint)softBytes.Length,
            Value = softBytes
        });

        // Set XMP
        image.Metadata.Xmp = @"<x:xmpmeta><rdf:RDF><rdf:Description dc:title=""RoundTripTest""/></rdf:RDF></x:xmpmeta>";

        string tempPath = Path.Combine(Path.GetTempPath(), $"all_meta_rt_{Guid.NewGuid()}.png");
        try
        {
            FormatRegistry.Write(image, tempPath);
            var reloaded = FormatRegistry.Read(tempPath);

            // Both should be preserved
            await Assert.That(reloaded.Metadata.ExifProfile).IsNotNull();
            await Assert.That(reloaded.Metadata.Xmp).IsNotNull();
            await Assert.That(reloaded.Metadata.Xmp).Contains("RoundTripTest");

            var soft = reloaded.Metadata.ExifProfile!.GetTag(ExifTag.Software);
            await Assert.That(soft).IsNotNull();
            await Assert.That(soft!.Value.GetString()).IsEqualTo("SharpImage");

            reloaded.Dispose();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        image.Dispose();
    }
}
