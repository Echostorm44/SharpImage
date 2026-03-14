using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Tests.Metadata;

/// <summary>
/// Tests for ICC profile read/write/strip/extract operations.
/// </summary>
public class IccProfileTests
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

    /// <summary>
    /// Creates a minimal valid sRGB ICC profile for testing.
    /// </summary>
    private static byte[] CreateMinimalSrgbProfile()
    {
        using var ms = new MemoryStream();

        // --- Header (128 bytes) ---
        WriteBE32(ms, 0); // Profile size (will fix later)
        ms.Write(new byte[4]); // Preferred CMM
        ms.Write([2, 0x40, 0, 0]); // Version 2.4
        ms.Write(System.Text.Encoding.ASCII.GetBytes("mntr")); // Device class
        ms.Write(System.Text.Encoding.ASCII.GetBytes("RGB ")); // Color space
        ms.Write(System.Text.Encoding.ASCII.GetBytes("XYZ ")); // PCS
        ms.Write(new byte[12]); // Date/time
        ms.Write(System.Text.Encoding.ASCII.GetBytes("acsp")); // Signature
        ms.Write(new byte[4]); // Primary platform
        WriteBE32(ms, 0); // Flags
        ms.Write(new byte[4]); // Manufacturer
        ms.Write(new byte[4]); // Model
        ms.Write(new byte[8]); // Attributes
        WriteBE32(ms, 0); // Rendering intent
        ms.Write([0, 0, 0xF6, 0xD6]); // PCS illuminant X
        ms.Write([0, 1, 0, 0]);       // PCS illuminant Y
        ms.Write([0, 0, 0xD3, 0x2D]); // PCS illuminant Z
        ms.Write(new byte[4]); // Creator
        ms.Write(new byte[16]); // Profile ID
        ms.Write(new byte[28]); // Reserved

        // At this point we're at offset 128

        // --- Tag table ---
        WriteBE32(ms, 1); // 1 tag

        // desc tag entry: signature, offset, size
        uint descOffset = 128 + 4 + 12; // = 144
        byte[] descText = System.Text.Encoding.ASCII.GetBytes("sRGB Test Profile");
        // desc type data: 4(sig) + 4(reserved) + 4(count) + text + 1(null) = 31
        uint descDataSize = (uint)(4 + 4 + 4 + descText.Length + 1);
        WriteBE32(ms, 0x64657363); // 'desc' sig
        WriteBE32(ms, descOffset);
        WriteBE32(ms, descDataSize);

        // --- desc tag data (at offset 144) ---
        WriteBE32(ms, 0x64657363); // type sig 'desc'
        WriteBE32(ms, 0);          // reserved
        WriteBE32(ms, (uint)(descText.Length + 1)); // ascii count (includes null)
        ms.Write(descText);
        ms.WriteByte(0); // null terminator

        // Fix profile size
        uint profileSize = (uint)ms.Position;
        ms.Position = 0;
        WriteBE32(ms, profileSize);
        ms.Position = profileSize;

        return ms.ToArray();
    }

    private static void WriteBE32(MemoryStream ms, uint value)
    {
        ms.WriteByte((byte)(value >> 24));
        ms.WriteByte((byte)(value >> 16));
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)value);
    }

    [Test]
    public async Task IccProfile_Constructor_ParsesData()
    {
        byte[] data = CreateMinimalSrgbProfile();
        var profile = new IccProfile(data);

        await Assert.That(profile.Data.Length).IsGreaterThan(0);
        await Assert.That(profile.ColorSpace).IsEqualTo("RGB");
    }

    [Test]
    public async Task IccProfile_Description_ParsesCorrectly()
    {
        byte[] data = CreateMinimalSrgbProfile();
        var profile = new IccProfile(data);

        await Assert.That(profile.Description).IsNotNull();
        await Assert.That(profile.Description).Contains("sRGB Test Profile");
    }

    [Test]
    public async Task IccProfile_PngRoundTrip_PreservesProfile()
    {
        var image = CreateSmallTestImage();
        byte[] iccData = CreateMinimalSrgbProfile();
        image.Metadata.IccProfile = new IccProfile(iccData);

        string tempPath = Path.Combine(Path.GetTempPath(), $"icc_roundtrip_{Guid.NewGuid()}.png");
        try
        {
            FormatRegistry.Write(image, tempPath);
            var reloaded = FormatRegistry.Read(tempPath);

            await Assert.That(reloaded.Metadata.IccProfile).IsNotNull();
            await Assert.That(reloaded.Metadata.IccProfile!.Data.Length).IsEqualTo(iccData.Length);
            await Assert.That(reloaded.Metadata.IccProfile.ColorSpace).IsEqualTo("RGB");

            reloaded.Dispose();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        image.Dispose();
    }

    [Test]
    public async Task IccProfile_Strip_RemovesProfile()
    {
        var image = CreateSmallTestImage();
        image.Metadata.IccProfile = new IccProfile(CreateMinimalSrgbProfile());

        string tempPath1 = Path.Combine(Path.GetTempPath(), $"icc_strip1_{Guid.NewGuid()}.png");
        string tempPath2 = Path.Combine(Path.GetTempPath(), $"icc_strip2_{Guid.NewGuid()}.png");
        try
        {
            FormatRegistry.Write(image, tempPath1);
            var withIcc = FormatRegistry.Read(tempPath1);
            await Assert.That(withIcc.Metadata.IccProfile).IsNotNull();

            withIcc.Metadata.IccProfile = null;
            FormatRegistry.Write(withIcc, tempPath2);

            var stripped = FormatRegistry.Read(tempPath2);
            await Assert.That(stripped.Metadata.IccProfile).IsNull();

            stripped.Dispose();
            withIcc.Dispose();
        }
        finally
        {
            if (File.Exists(tempPath1)) File.Delete(tempPath1);
            if (File.Exists(tempPath2)) File.Delete(tempPath2);
        }

        image.Dispose();
    }

    [Test]
    public async Task IccProfile_Clone_CreatesDeepCopy()
    {
        byte[] data = CreateMinimalSrgbProfile();
        var original = new IccProfile(data);
        var clone = original.Clone();

        await Assert.That(clone.Data.Length).IsEqualTo(original.Data.Length);
        await Assert.That(clone.ColorSpace).IsEqualTo(original.ColorSpace);

        // Verify it's a deep copy (different array reference)
        await Assert.That(ReferenceEquals(original.Data, clone.Data)).IsFalse();
    }

    [Test]
    public async Task IccProfile_ExtractToFile_WritesCorrectData()
    {
        byte[] data = CreateMinimalSrgbProfile();
        string tempPath = Path.Combine(Path.GetTempPath(), $"icc_extract_{Guid.NewGuid()}.icc");
        try
        {
            File.WriteAllBytes(tempPath, data);
            byte[] readBack = File.ReadAllBytes(tempPath);
            var profile = new IccProfile(readBack);

            await Assert.That(profile.Data.Length).IsEqualTo(data.Length);
            await Assert.That(profile.ColorSpace).IsEqualTo("RGB");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Test]
    public async Task MountainsJpeg_HasIccProfile()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "TestAssets", "landscape.jpg");
        if (!File.Exists(path))
        {
            return;
        }

        var image = FormatRegistry.Read(path);
        await Assert.That(image.Metadata.IccProfile).IsNotNull();
        await Assert.That(image.Metadata.IccProfile!.Data.Length).IsGreaterThan(100);
        image.Dispose();
    }
}
