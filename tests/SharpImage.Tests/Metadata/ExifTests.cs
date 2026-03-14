using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Tests.Metadata;

/// <summary>
/// Tests for EXIF metadata read/write/strip operations.
/// </summary>
public class ExifTests
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
                row[offset] = (ushort)(x * 4096);     // R
                row[offset + 1] = (ushort)(y * 4096); // G
                row[offset + 2] = 32768;               // B
            }
        }
        return img;
    }

    [Test]
    public async Task ExifProfile_SetAndGetTag_RoundTrips()
    {
        var profile = new ExifProfile { IsLittleEndian = true };
        byte[] makeBytes = System.Text.Encoding.ASCII.GetBytes("TestCamera\0");
        profile.SetTag(new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)makeBytes.Length,
            Value = makeBytes
        });

        var retrieved = profile.GetTag(ExifTag.Make);
        await Assert.That(retrieved).IsNotNull();
        await Assert.That(retrieved!.Value.GetString()).IsEqualTo("TestCamera");
    }

    [Test]
    public async Task ExifProfile_RemoveTag_RemovesSuccessfully()
    {
        var profile = new ExifProfile { IsLittleEndian = true };
        byte[] makeBytes = System.Text.Encoding.ASCII.GetBytes("TestCamera\0");
        profile.SetTag(new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)makeBytes.Length,
            Value = makeBytes
        });

        bool removed = profile.RemoveTag(ExifTag.Make);
        await Assert.That(removed).IsTrue();
        await Assert.That(profile.GetTag(ExifTag.Make)).IsNull();
    }

    [Test]
    public async Task ExifProfile_GetAllTags_ReturnsAllIFDs()
    {
        var profile = new ExifProfile { IsLittleEndian = true };

        byte[] makeBytes = System.Text.Encoding.ASCII.GetBytes("TestCamera\0");
        profile.Ifd0Tags.Add(new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)makeBytes.Length,
            Value = makeBytes
        });

        byte[] focalBytes = new byte[8];
        BitConverter.TryWriteBytes(focalBytes.AsSpan(0, 4), 50u);
        BitConverter.TryWriteBytes(focalBytes.AsSpan(4, 4), 1u);
        profile.ExifTags.Add(new ExifEntry
        {
            Tag = ExifTag.FocalLength,
            DataType = ExifDataType.Rational,
            Count = 1,
            Value = focalBytes
        });

        var allTags = profile.GetAllTags().ToList();
        await Assert.That(allTags.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task ExifProfile_SerializeForPngExif_RoundTrips()
    {
        var profile = new ExifProfile { IsLittleEndian = true };
        byte[] makeBytes = System.Text.Encoding.ASCII.GetBytes("TestCamera\0");
        profile.SetTag(new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)makeBytes.Length,
            Value = makeBytes
        });

        byte[] serialized = ExifParser.SerializeForPngExif(profile);
        await Assert.That(serialized.Length).IsGreaterThan(8);

        // Parse it back
        var parsed = ExifParser.ParseFromTiff(serialized);
        await Assert.That(parsed).IsNotNull();
        var make = parsed!.GetTag(ExifTag.Make);
        await Assert.That(make).IsNotNull();
        await Assert.That(make!.Value.GetString()).IsEqualTo("TestCamera");
    }

    [Test]
    public async Task ExifProfile_PngRoundTrip_PreservesExif()
    {
        var image = CreateSmallTestImage();
        image.Metadata.ExifProfile = new ExifProfile { IsLittleEndian = true };
        image.Metadata.ExifProfile.RawData = null; // Force serialization from tags

        byte[] softwareBytes = System.Text.Encoding.ASCII.GetBytes("SharpImage Test\0");
        image.Metadata.ExifProfile.SetTag(new ExifEntry
        {
            Tag = ExifTag.Software,
            DataType = ExifDataType.Ascii,
            Count = (uint)softwareBytes.Length,
            Value = softwareBytes
        });

        string tempPath = Path.Combine(Path.GetTempPath(), $"exif_roundtrip_{Guid.NewGuid()}.png");
        try
        {
            FormatRegistry.Write(image, tempPath);
            var reloaded = FormatRegistry.Read(tempPath);

            await Assert.That(reloaded.Metadata.ExifProfile).IsNotNull();
            var software = reloaded.Metadata.ExifProfile!.GetTag(ExifTag.Software);
            await Assert.That(software).IsNotNull();
            await Assert.That(software!.Value.GetString()).IsEqualTo("SharpImage Test");

            reloaded.Dispose();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        image.Dispose();
    }

    [Test]
    public async Task ExifProfile_StripAndSave_NoExifInOutput()
    {
        var image = CreateSmallTestImage();
        image.Metadata.ExifProfile = new ExifProfile { IsLittleEndian = true };
        image.Metadata.ExifProfile.RawData = null;

        byte[] makeBytes = System.Text.Encoding.ASCII.GetBytes("ShouldBeStripped\0");
        image.Metadata.ExifProfile.SetTag(new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)makeBytes.Length,
            Value = makeBytes
        });

        // Write with EXIF, then strip and write again
        string tempPath1 = Path.Combine(Path.GetTempPath(), $"exif_strip1_{Guid.NewGuid()}.png");
        string tempPath2 = Path.Combine(Path.GetTempPath(), $"exif_strip2_{Guid.NewGuid()}.png");
        try
        {
            FormatRegistry.Write(image, tempPath1);
            var reloaded = FormatRegistry.Read(tempPath1);
            await Assert.That(reloaded.Metadata.ExifProfile).IsNotNull();

            reloaded.Metadata.ExifProfile = null;
            FormatRegistry.Write(reloaded, tempPath2);

            var stripped = FormatRegistry.Read(tempPath2);
            await Assert.That(stripped.Metadata.ExifProfile).IsNull();

            stripped.Dispose();
            reloaded.Dispose();
        }
        finally
        {
            if (File.Exists(tempPath1)) File.Delete(tempPath1);
            if (File.Exists(tempPath2)) File.Delete(tempPath2);
        }

        image.Dispose();
    }

    [Test]
    public async Task ExifEntry_FormatValue_FormatsAsciiCorrectly()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("Hello World\0");
        var entry = new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)bytes.Length,
            Value = bytes
        };

        string formatted = entry.FormatValue(littleEndian: true);
        await Assert.That(formatted).Contains("Hello World");
    }

    [Test]
    public async Task ExifEntry_FormatValue_FormatsRationalCorrectly()
    {
        byte[] bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), 100u);
        var entry = new ExifEntry
        {
            Tag = ExifTag.ExposureTime,
            DataType = ExifDataType.Rational,
            Count = 1,
            Value = bytes
        };

        string formatted = entry.FormatValue(littleEndian: true);
        // FormatValue outputs rationals as decimal
        await Assert.That(formatted).Contains("0.01");
    }

    [Test]
    public async Task ExifProfile_MultipleTagTypes_AllPreserved()
    {
        var profile = new ExifProfile { IsLittleEndian = true };

        // ASCII tag
        byte[] makeBytes = System.Text.Encoding.ASCII.GetBytes("TestCamera\0");
        profile.Ifd0Tags.Add(new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)makeBytes.Length,
            Value = makeBytes
        });

        // Short tag (Orientation)
        byte[] orientBytes = new byte[2];
        BitConverter.TryWriteBytes(orientBytes, (ushort)6);
        profile.Ifd0Tags.Add(new ExifEntry
        {
            Tag = ExifTag.Orientation,
            DataType = ExifDataType.Short,
            Count = 1,
            Value = orientBytes
        });

        await Assert.That(profile.Ifd0Tags.Count).IsEqualTo(2);
        await Assert.That(profile.GetTag(ExifTag.Make)).IsNotNull();
        await Assert.That(profile.GetTag(ExifTag.Orientation)).IsNotNull();
        await Assert.That(profile.GetTag(ExifTag.Orientation)!.Value.GetUInt16()).IsEqualTo((ushort)6);
    }

    [Test]
    public async Task ExifTag_GetName_ReturnsKnownNames()
    {
        await Assert.That(ExifTag.GetName(ExifTag.Make)).IsEqualTo("Make");
        await Assert.That(ExifTag.GetName(ExifTag.Model)).IsEqualTo("Model");
        await Assert.That(ExifTag.GetName(ExifTag.Software)).IsEqualTo("Software");
        await Assert.That(ExifTag.GetName(ExifTag.FocalLength)).IsEqualTo("FocalLength");
    }

    [Test]
    public async Task ExifProfile_SetTag_OverwritesExisting()
    {
        var profile = new ExifProfile { IsLittleEndian = true };

        byte[] v1 = System.Text.Encoding.ASCII.GetBytes("Canon\0");
        profile.SetTag(new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)v1.Length,
            Value = v1
        });

        byte[] v2 = System.Text.Encoding.ASCII.GetBytes("Nikon\0");
        profile.SetTag(new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)v2.Length,
            Value = v2
        });

        var result = profile.GetTag(ExifTag.Make);
        await Assert.That(result!.Value.GetString()).IsEqualTo("Nikon");
        await Assert.That(profile.Ifd0Tags.Count(t => t.Tag == ExifTag.Make)).IsEqualTo(1);
    }

    [Test]
    public async Task ExifProfile_Clone_CreatesDeepCopy()
    {
        var profile = new ExifProfile { IsLittleEndian = true };
        byte[] makeBytes = System.Text.Encoding.ASCII.GetBytes("TestCamera\0");
        profile.SetTag(new ExifEntry
        {
            Tag = ExifTag.Make,
            DataType = ExifDataType.Ascii,
            Count = (uint)makeBytes.Length,
            Value = makeBytes
        });

        var clone = profile.Clone();
        clone.RemoveTag(ExifTag.Make);

        // Original should still have it
        await Assert.That(profile.GetTag(ExifTag.Make)).IsNotNull();
        await Assert.That(clone.GetTag(ExifTag.Make)).IsNull();
    }

    [Test]
    public async Task RealJpeg_MountainsHasIccProfile()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "TestAssets", "landscape.jpg");
        if (!File.Exists(path))
        {
            return;
        }

        var image = FormatRegistry.Read(path);
        // landscape.jpg has EXIF metadata
        await Assert.That(image.Metadata.IccProfile).IsNotNull();
        await Assert.That(image.Metadata.IccProfile!.Data.Length).IsGreaterThan(0);
        image.Dispose();
    }
}
